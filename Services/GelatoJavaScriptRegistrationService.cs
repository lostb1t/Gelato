using System.Reflection;
using System.Runtime.Loader;
using Gelato.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Gelato.Services
{
    /// <summary>
    /// Hosted service that registers bundled frontend JavaScript files (embedded resources under Frontend/js)
    /// with a JavaScript registration service if available. Reacts to configuration changes to (un)register scripts.
    /// </summary>
    public class GelatoJavaScriptRegistrationService(
        IOptionsMonitor<PluginConfiguration> optionsMonitor,
        ILogger<GelatoJavaScriptRegistrationService> logger
    ) : IHostedService, IDisposable
    {
        private readonly Lock _lock = new();
        private bool _registered;
        private Type? _pluginInterfaceType;
        private IDisposable? _onChangeToken;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Resolve injector service if available
            try
            {
                var jsInjectorAssembly = AssemblyLoadContext
                    .All.SelectMany(x => x.Assemblies)
                    .FirstOrDefault(x =>
                        x.FullName?.Contains("Jellyfin.Plugin.JavaScriptInjector") ?? false
                    );

                if (jsInjectorAssembly != null)
                {
                    _pluginInterfaceType = jsInjectorAssembly.GetType(
                        "Jellyfin.Plugin.JavaScriptInjector.PluginInterface"
                    );
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Error resolving IJavaScriptRegistrationService at StartAsync"
                );
                _pluginInterfaceType = null;
            }

            // Subscribe to configuration changes via IOptionsMonitor (if available)
            try
            {
                _onChangeToken = optionsMonitor.OnChange(OnConfigChanged);
            }
            catch (Exception)
            { /* ignore if options monitor not wired */
            }

            // Also subscribe to plugin-level configuration update events (fired by GelatoPlugin.UpdateConfiguration)
            try
            {
                GelatoPlugin.ConfigurationChanged += OnConfigChanged;
            }
            catch (Exception)
            { /* ignore if plugin static event not available */
            }

            // Register according to current config - prefer plugin instance config if available
            try
            {
                var cfg = GelatoPlugin.Instance?.Configuration ?? optionsMonitor.CurrentValue;
                UpdateRegistration(cfg);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during initial JS registration");
            }

            return Task.CompletedTask;
        }

        private void OnConfigChanged(PluginConfiguration cfg)
        {
            try
            {
                UpdateRegistration(cfg);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error handling plugin configuration change");
            }
        }

        private void UpdateRegistration(PluginConfiguration cfg)
        {
            lock (_lock)
            {
                if (!cfg.EnableJavaScriptInjection)
                {
                    if (_registered)
                    {
                        UnregisterBundledScripts();
                        _registered = false;
                    }
                    else
                    {
                        logger.LogDebug("JS injection disabled; nothing to unregister.");
                    }
                    return;
                }

                // Enabled in config
                if (_registered)
                {
                    // already registered: re-register to update content (best-effort)
                    UnregisterBundledScripts();
                }

                RegisterBundledScripts();
                _registered = true;
            }
        }

        private void RegisterBundledScripts()
        {
            if (_pluginInterfaceType is null)
            {
                logger.LogInformation(
                    "IJavaScriptRegistrationService not available; skipping JS registration."
                );
                return;
            }

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resources = assembly
                    .GetManifestResourceNames()
                    .Where(n =>
                        n.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                        && n.Contains(".Frontend.")
                    )
                    .OrderBy(n => n)
                    .ToList();

                if (resources.Count == 0)
                {
                    logger.LogInformation("No embedded frontend JS resources found to register.");
                    return;
                }

                var pluginId = GelatoPlugin.Instance?.Id.ToString() ?? "gelato-plugin";
                var pluginName = GelatoPlugin.Instance?.Name ?? "Gelato";
                var pluginVersion =
                    GelatoPlugin.Instance?.GetType().Assembly.GetName().Version?.ToString()
                    ?? "unknown";

                foreach (var res in resources)
                {
                    try
                    {
                        using var stream = assembly.GetManifestResourceStream(res);
                        if (stream is null)
                        {
                            logger.LogWarning(
                                "Embedded resource {Resource} could not be opened.",
                                res
                            );
                            continue;
                        }

                        using var reader = new StreamReader(stream);
                        var content = reader.ReadToEnd();

                        var scriptRegistration = new JObject
                        {
                            { "id", $"{res}-{pluginId}" },
                            { "name", res },
                            { "script", content },
                            { "enabled", true },
                            { "requiresAuthentication", false },
                            { "pluginId", pluginId },
                            { "pluginName", pluginName },
                            { "pluginVersion", pluginVersion },
                        };

                        // Register the script
                        var registerResult = _pluginInterfaceType
                            .GetMethod("RegisterScript")
                            ?.Invoke(null, [scriptRegistration]);

                        if (registerResult is true)
                        {
                            logger.LogInformation(
                                "Successfully registered {Resource} with JavaScript Injector plugin.",
                                res
                            );
                        }
                        else
                        {
                            logger.LogWarning(
                                "Failed to register {Resource} with JavaScript Injector plugin. RegisterScript returned false.",
                                res
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "Error registering embedded JS resource {Resource}",
                            res
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Exception while attempting to register embedded frontend JS files"
                );
            }
        }

        private void UnregisterBundledScripts()
        {
            if (_pluginInterfaceType is null)
            {
                logger.LogInformation(
                    "IJavaScriptRegistrationService not available; nothing to unregister."
                );
                return;
            }

            try
            {
                if (_pluginInterfaceType != null)
                {
                    var pluginId = GelatoPlugin.Instance?.Id.ToString() ?? "gelato-plugin";
                    var unregisterResult = _pluginInterfaceType
                        .GetMethod("UnregisterAllScriptsFromPlugin")
                        ?.Invoke(null, [pluginId]);

                    if (unregisterResult is int removedCount)
                    {
                        logger.LogInformation(
                            "Successfully unregistered {Count} script(s) from JavaScript Injector plugin.",
                            removedCount
                        );
                    }
                    else
                    {
                        logger.LogWarning(
                            "Failed to unregister scripts from JavaScript Injector plugin. Method returned unexpected value."
                        );
                    }
                }

                logger.LogInformation(
                    "No suitable unregister method found on IJavaScriptRegistrationService; cannot automatically unregister resources."
                );
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Exception while attempting to unregister embedded frontend JS files"
                );
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _onChangeToken?.Dispose();
                try
                {
                    GelatoPlugin.ConfigurationChanged -= OnConfigChanged;
                }
                catch (Exception)
                {
                    // ignored
                }
                if (_registered)
                {
                    UnregisterBundledScripts();
                    _registered = false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during JS unregister on StopAsync");
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _onChangeToken?.Dispose();
            try
            {
                GelatoPlugin.ConfigurationChanged -= OnConfigChanged;
            }
            catch (Exception)
            {
                // ignored
            }
        }
    }
}
