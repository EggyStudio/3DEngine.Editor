using Editor.Server;
using Editor.Shell;
using Microsoft.AspNetCore.Builder;

namespace Engine;

/// <summary>
/// Composite plugin that brings up the in-process editor:
/// <list type="bullet">
///   <item><description>A <see cref="ShellRegistry"/> + hot-reload <see cref="ShellCompiler"/> watching a script directory.</description></item>
///   <item><description>The <see cref="EditorServerHost"/> Blazor Server, started in-process.</description></item>
///   <item><description>A <see cref="WebViewPlugin"/> that renders the Blazor URL inside the engine's window.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Single process  the Blazor Server runs in-process on a background thread while the
/// SDL3/Vulkan engine drives the main thread. The editor UI is rendered via an
/// Ultralight webview overlay composited into the Vulkan render pipeline. Play mode
/// can open a separate SDL3 window for the full game runtime.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// new App(Config.GetDefault(title: "3D Engine Editor", width: 1920, height: 1080))
///     .AddPlugin(new DefaultPlugins())
///     .AddPlugin(new EditorPlugin())
///     .Run();
/// </code>
/// </example>
/// <seealso cref="WebViewPlugin"/>
/// <seealso cref="ShellCompiler"/>
/// <seealso cref="EditorServerHost"/>
public sealed class EditorPlugin : IPlugin
{
    private static readonly ILogger Logger = Log.Category("Engine.Editor");

    /// <summary>URL the in-process Blazor Server listens on. Defaults to <c>http://localhost:5000</c>.</summary>
    public string ServerUrl { get; init; } = "http://localhost:5000";

    /// <summary>
    /// Directory the <see cref="ShellCompiler"/> watches for hot-reloadable shell scripts.
    /// Defaults to <c>{AppContext.BaseDirectory}/source/shells</c> (matches the build-staged layout
    /// produced by the engine's <c>Modules\**</c> Content glob).
    /// </summary>
    public string? ScriptsDirectory { get; init; }

    /// <inheritdoc />
    public void Build(App app)
    {
        Logger.Info("EditorPlugin: Building...");

        // -- 1. Shell registry + hot-reload script compiler --
        var scriptsDir = ScriptsDirectory ?? Path.Combine(AppContext.BaseDirectory, "source", "shells");

        var registry = new ShellRegistry();
        var compiler = new ShellCompiler(registry)
            .WatchDirectory(scriptsDir)
            .AddReference(typeof(App).Assembly)             // Engine (3DEngine.dll)
            .AddReference(typeof(ShellRegistry).Assembly);  // 3DEngine.Server.dll (Editor.Shell types)

        // EcsWorld is currently in the same assembly as App, but keep this guarded
        // so the plugin still works if it ever moves out.
        try { compiler.AddReference(typeof(EcsWorld).Assembly); }
        catch (Exception ex) { Logger.Debug($"EcsWorld reference skipped: {ex.Message}"); }

        var initial = compiler.Start();
        Logger.Info($"Script compilation: {initial.Message}");
        foreach (var err in initial.Errors)
            Logger.Error($"  {err.FileName}({err.Line},{err.Column}): {err.Message}");

        compiler.CompilationCompleted += result =>
        {
            Logger.Info($"Hot-reload: {result.Message}");
            foreach (var err in result.Errors)
                Logger.Error($"  {err.FileName}({err.Line},{err.Column}): {err.Message}");
        };

        // -- 2. Blazor Server in-process (non-blocking after this awaits the listener bind) --
        WebApplication server;
        try
        {
            server = EditorServerHost.StartAsync(ServerUrl, registry: registry).GetAwaiter().GetResult();
            Logger.Info($"Blazor Server listening on {ServerUrl}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start Blazor Server", ex);
            compiler.Dispose();
            throw;
        }

        // Expose for downstream systems / debugging via the world resources.
        app.World.InsertResource(registry);
        app.World.InsertResource(compiler);

        // -- 3. Embed the Blazor UI in the engine window via the existing WebView plugin --
        app.AddPlugin(new WebViewPlugin { InitialUrl = ServerUrl });

        // -- 4. Graceful shutdown when the engine window closes --
        app.AddSystem(Stage.Cleanup, new SystemDescriptor(_ =>
        {
            Logger.Info("Shutting down editor...");
            try { compiler.Dispose(); }
            catch (Exception ex) { Logger.Warn($"ShellCompiler dispose failed: {ex.Message}"); }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                server.StopAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Logger.Warn($"Blazor Server stop failed: {ex.Message}"); }

            Logger.Info("Editor shut down cleanly.");
        }, "EditorPlugin.Cleanup").MainThreadOnly());

        Logger.Info("EditorPlugin: Build complete.");
    }
}

