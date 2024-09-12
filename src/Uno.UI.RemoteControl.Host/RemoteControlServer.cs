﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Uno.Extensions;
using Uno.UI.RemoteControl.Helpers;
using Uno.UI.RemoteControl.Host.IdeChannel;
using Uno.UI.RemoteControl.HotReload.Messages;
using Uno.UI.RemoteControl.Messages;
using Uno.UI.RemoteControl.Messaging.IdeChannel;

namespace Uno.UI.RemoteControl.Host;

internal class RemoteControlServer : IRemoteControlServer, IDisposable
{
	private readonly object _loadContextGate = new();
	private static readonly Dictionary<string, (AssemblyLoadContext Context, int Count)> _loadContexts = new();
	private static readonly Dictionary<string, string> _resolveAssemblyLocations = new();
	private readonly Dictionary<string, IServerProcessor> _processors = new();
	private readonly CancellationTokenSource _ct = new();

	private WebSocket? _socket;
	private IdeChannelServer? _ideChannelServer;
	private readonly List<string> _appInstanceIds = new();
	private readonly IConfiguration _configuration;
	private readonly IIdeChannelServerProvider _ideChannelProvider;
	private readonly IServiceProvider _serviceProvider;

	public RemoteControlServer(IConfiguration configuration, IIdeChannelServerProvider ideChannelProvider, IServiceProvider serviceProvider)
	{
		_configuration = configuration;
		_ideChannelProvider = ideChannelProvider;
		_serviceProvider = serviceProvider;

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().LogDebug("Starting RemoteControlServer");
		}
	}

	string IRemoteControlServer.GetServerConfiguration(string key)
		=> _configuration[key] ?? "";

	private AssemblyLoadContext GetAssemblyLoadContext(string applicationId)
	{
		lock (_loadContextGate)
		{
			if (_loadContexts.TryGetValue(applicationId, out var lc))
			{
				_loadContexts[applicationId] = (lc.Context, lc.Count + 1);

				return lc.Context;
			}

			var loadContext = new AssemblyLoadContext(applicationId, isCollectible: true);
			loadContext.Unloading += (e) =>
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug("Unloading assembly context {name}", e.Name);
				}
			};

			// Add custom resolving so we can find dependencies even when the processor assembly
			// is built for a different .net version than the host process.
			loadContext.Resolving += (context, assemblyName) =>
			{
				if (_resolveAssemblyLocations.TryGetValue(applicationId, out var _resolveAssemblyLocation) &&
					!string.IsNullOrWhiteSpace(_resolveAssemblyLocation))
				{
					try
					{
						var dir = Path.GetDirectoryName(_resolveAssemblyLocation);
						if (!string.IsNullOrEmpty(dir))
						{
							var relPath = Path.Combine(dir, assemblyName.Name + ".dll");
							if (File.Exists(relPath))
							{
								if (this.Log().IsEnabled(LogLevel.Trace))
								{
									this.Log().LogTrace("Loading assembly from resolved path: {relPath}", relPath);
								}

								using var fs = File.Open(relPath, FileMode.Open, FileAccess.Read, FileShare.Read);
								return context.LoadFromStream(fs);
							}
						}
					}
					catch (Exception exc)
					{
						if (this.Log().IsEnabled(LogLevel.Error))
						{
							this.Log().LogError(exc, "Failed for load dependency: {assemblyName}", assemblyName);
						}
					}
				}
				else
				{
					if (this.Log().IsEnabled(LogLevel.Debug))
					{
						this.Log().LogDebug("Failed for identify location of dependency: {assemblyName}", assemblyName);
					}
				}

				// We haven't found the assembly in our context, let the runtime
				// find it using standard resolution mechanisms.
				return null;
			};

			if (!_loadContexts.TryAdd(applicationId, (loadContext, 1)))
			{
				if (this.Log().IsEnabled(LogLevel.Trace))
				{
					this.Log().LogTrace("Failed to add a LoadContext for : {appId}", applicationId);
				}
			}

			return loadContext;
		}
	}

	private void RegisterProcessor(IServerProcessor hotReloadProcessor)
		=> _processors[hotReloadProcessor.Scope] = hotReloadProcessor;

	public IdeChannelServer? IDEChannelServer => _ideChannelServer;

	public async Task RunAsync(WebSocket socket, CancellationToken ct)
	{
		_socket = socket;

		await TryStartIDEChannelAsync();

		while (await WebSocketHelper.ReadFrame(socket, ct) is Frame frame)
		{
			if (frame.Scope == "RemoteControlServer")
			{
				if (frame.Name == ProcessorsDiscovery.Name)
				{
					await ProcessDiscoveryFrame(frame);
					continue;
				}

				if (frame.Name == KeepAliveMessage.Name)
				{
					await ProcessPingFrame(frame);
					continue;
				}
			}

			if (_processors.TryGetValue(frame.Scope, out var processor))
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug("Received Frame [{Scope} / {Name}] to be processed by {processor}", frame.Scope, frame.Name, processor);
				}

				try
				{
					DevServerDiagnostics.Current = DiagnosticsSink.Instance;
					await processor.ProcessFrame(frame);
				}
				catch (Exception e)
				{
					if (this.Log().IsEnabled(LogLevel.Error))
					{
						this.Log().LogError(e, "Failed to process frame [{Scope} / {Name}]", frame.Scope, frame.Name);
					}
				}
			}
			else
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().LogDebug("Unknown Frame [{Scope} / {Name}]", frame.Scope, frame.Name);
				}
			}
		}
	}

	private async Task TryStartIDEChannelAsync()
	{
		if (_ideChannelServer is { } oldChannel)
		{
			oldChannel.MessageFromIDE -= ProcessIdeMessage;
		}

		_ideChannelServer = await _ideChannelProvider.GetIdeChannelServerAsync();

		if (_ideChannelServer is { } newChannel)
		{
			newChannel.MessageFromIDE += ProcessIdeMessage;
		}
	}

	private void ProcessIdeMessage(object? sender, IdeMessage message)
	{
		if (_processors.TryGetValue(message.Scope, out var processor))
		{
			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().LogTrace("Received message [{Scope} / {Name}] to be processed by {processor}", message.Scope, message.GetType().Name, processor);
			}

			var process = processor.ProcessIdeMessage(message, _ct.Token);

			if (this.Log().IsEnabled(LogLevel.Error))
			{
				process = process.ContinueWith(
					t => this.Log().LogError($"Failed to process message {message}: {t.Exception?.Flatten()}"),
					_ct.Token,
					TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.AttachedToParent,
					TaskScheduler.Default);
			}
		}
		else
		{
			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().LogTrace("Unknown Frame [{Scope} / {Name}]", message.Scope, message.GetType().Name);
			}
		}
	}

	private async Task ProcessPingFrame(Frame frame)
	{
		KeepAliveMessage pong;
		if (frame.TryGetContent(out KeepAliveMessage? ping))
		{
			pong = new() { SequenceId = ping.SequenceId };

			if (ping.AssemblyVersion != pong.AssemblyVersion && this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().LogWarning(
					$"Client ping frame (a.k.a. KeepAlive), but version differs from server (server: {pong.AssemblyVersion} | client: {ping.AssemblyVersion})."
					+ $"This usually indicates that an old instance of the dev-server is being re-used or a partial deployment of the application."
					+ "Some feature like hot-reload are most likely to fail. To fix this, you might have to restart Visual Studio.");
			}
			else if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().LogTrace($"Client ping frame (a.k.a. KeepAlive) with valid version ({ping.AssemblyVersion}).");
			}
		}
		else
		{
			pong = new();

			if (this.Log().IsEnabled(LogLevel.Warning))
			{
				this.Log().LogWarning(
					"Client ping frame (a.k.a. KeepAlive), but failed to deserialize it's content. "
					+ $"This usually indicates a version mismatch between client and server (server: {pong.AssemblyVersion})."
					+ "Some feature like hot-reload are most likely to fail. To fix this, you might have to restart Visual Studio.");
			}
		}

		await SendFrame(pong);
	}

	private async Task ProcessDiscoveryFrame(Frame frame)
	{
		var assemblies = new List<(string path, System.Reflection.Assembly assembly)>();
		var discoveredProcessors = new List<DiscoveredProcessor>();
		try
		{
			var msg = JsonConvert.DeserializeObject<ProcessorsDiscovery>(frame.Content)!;
			var serverAssemblyName = typeof(IServerProcessor).Assembly.GetName().Name;

			if (!_appInstanceIds.Contains(msg.AppInstanceId))
			{
				_appInstanceIds.Add(msg.AppInstanceId);
			}

			var assemblyLoadContext = GetAssemblyLoadContext(msg.AppInstanceId);

			// If BasePath is a specific file, try and load that
			if (File.Exists(msg.BasePath))
			{
				try
				{
					_resolveAssemblyLocations[msg.AppInstanceId] = msg.BasePath;

					using var fs = File.Open(msg.BasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
					assemblies.Add((msg.BasePath, assemblyLoadContext.LoadFromStream(fs)));
				}
				catch (Exception exc)
				{
					if (this.Log().IsEnabled(LogLevel.Error))
					{
						this.Log().LogError("Failed to load assembly {BasePath} : {Exc}", msg.BasePath, exc);
					}
				}
			}
			else
			{
				// As BasePath is a directory, try and load processors from assemblies within that dir
				var basePath = msg.BasePath.Replace('/', Path.DirectorySeparatorChar);

#if NET9_0_OR_GREATER
				basePath = Path.Combine(basePath, "net9.0");
#elif NET8_0_OR_GREATER
				basePath = Path.Combine(basePath, "net8.0");
#endif

				// Additional processors may not need the directory added immediately above.
				if (!Directory.Exists(basePath))
				{
					basePath = msg.BasePath;
				}

				foreach (var file in Directory.GetFiles(basePath, "Uno.*.dll"))
				{
					if (Path.GetFileNameWithoutExtension(file).Equals(serverAssemblyName, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					if (this.Log().IsEnabled(LogLevel.Debug))
					{
						this.Log().LogDebug("Discovery: Loading {File}", file);
					}

					try
					{
						assemblies.Add((file, assemblyLoadContext.LoadFromAssemblyPath(file)));
					}
					catch (Exception exc)
					{
						// With additional processors there may be duplicates of assemblies already loaded
						if (this.Log().IsEnabled(LogLevel.Debug))
						{
							this.Log().LogDebug("Failed to load assembly {File} : {Exc}", file, exc);
						}
					}
				}
			}

			foreach (var asm in assemblies)
			{
				try
				{
					if (assemblies.Count > 1 ||
						!_resolveAssemblyLocations.TryGetValue(msg.AppInstanceId, out var _resolveAssemblyLocation) ||
						string.IsNullOrEmpty(_resolveAssemblyLocation))
					{
						_resolveAssemblyLocations[msg.AppInstanceId] = asm.path;
					}

					var attributes = asm.assembly.GetCustomAttributes(typeof(ServerProcessorAttribute), false);

					foreach (var processorAttribute in attributes)
					{
						if (processorAttribute is ServerProcessorAttribute processor)
						{
							if (this.Log().IsEnabled(LogLevel.Debug))
							{
								this.Log().LogDebug("Discovery: Registering {ProcessorType}", processor.ProcessorType);
							}

							try
							{
								if (asm.assembly.CreateInstance(processor.ProcessorType.FullName!, ignoreCase: false, bindingAttr: BindingFlags.Instance | BindingFlags.Public, binder: null, args: new[] { this }, culture: null, activationAttributes: null) is IServerProcessor serverProcessor)
								{
									discoveredProcessors.Add(new(asm.path, processor.ProcessorType.FullName!, VersionHelper.GetVersion(processor.ProcessorType), IsLoaded: true));
									RegisterProcessor(serverProcessor);
								}
								else
								{
									discoveredProcessors.Add(new(asm.path, processor.ProcessorType.FullName!, VersionHelper.GetVersion(processor.ProcessorType), IsLoaded: false));
									if (this.Log().IsEnabled(LogLevel.Debug))
									{
										this.Log().LogDebug("Failed to create server processor {ProcessorType}", processor.ProcessorType);
									}
								}
							}
							catch (Exception error)
							{
								discoveredProcessors.Add(new(asm.path, processor.ProcessorType.FullName!, VersionHelper.GetVersion(processor.ProcessorType), IsLoaded: false, LoadError: error.ToString()));
								if (this.Log().IsEnabled(LogLevel.Error))
								{
									this.Log().LogError(error, "Failed to create server processor {ProcessorType}", processor.ProcessorType);
								}
							}
						}
					}
				}
				catch (Exception exc)
				{
					if (this.Log().IsEnabled(LogLevel.Error))
					{
						this.Log().LogError("Failed to create instance of server processor in  {Asm} : {Exc}", asm, exc);
					}
				}
			}

			// Being thorough about trying to ensure everything is unloaded
			assemblies.Clear();
		}
		catch (Exception exc)
		{
			if (this.Log().IsEnabled(LogLevel.Error))
			{
				this.Log().LogError("Failed to process discovery frame: {Exc}", exc);
			}
		}
		finally
		{
			await SendFrame(new ProcessorsDiscoveryResponse(
				assemblies.Select(asm => asm.path).ToImmutableList(),
				discoveredProcessors.ToImmutableList()));
		}
	}

	public async Task SendFrame(IMessage message)
	{
		if (_socket is not null)
		{
			await WebSocketHelper.SendFrame(
				_socket,
				Frame.Create(
					1,
					message.Scope,
					message.Name,
					message
					),
				CancellationToken.None);
		}
		else
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().LogDebug($"Failed to send, no connection available");
			}
		}
	}

	public async Task SendMessageToIDEAsync(IdeMessage message)
	{
		if (IDEChannelServer is not null)
		{
			await IDEChannelServer.SendToIdeAsync(message);
		}
	}

	public void Dispose()
	{
		_ct.Cancel(false);

		foreach (var processor in _processors)
		{
			processor.Value.Dispose();
		}

		// Unload any AssemblyLoadContexts not being used by any current connection
		foreach (var appId in _appInstanceIds)
		{
			lock (_loadContextGate)
			{
				if (_loadContexts.TryGetValue(appId, out var lc))
				{
					if (lc.Count > 1)
					{
						_loadContexts[appId] = (lc.Context, lc.Count - 1);
					}
					else
					{
						try
						{
							_loadContexts[appId].Context.Unload();

							_loadContexts.Remove(appId);
						}
						catch (Exception exc)
						{
							if (this.Log().IsEnabled(LogLevel.Error))
							{
								this.Log().LogError("Failed to unload AssemblyLoadContext for '{appId}' : {Exc}", appId, exc);
							}
						}
					}
				}
			}
		}
	}

	private class DiagnosticsSink : DevServerDiagnostics.ISink
	{
		public static DiagnosticsSink Instance { get; } = new();

		private DiagnosticsSink() { }

		/// <inheritdoc />
		public void ReportInvalidFrame<TContent>(Frame frame)
			=> typeof(RemoteControlServer).Log().LogError($"Got an invalid frame for type {typeof(TContent).Name} [{frame.Scope} / {frame.Name}]");
	}

}
