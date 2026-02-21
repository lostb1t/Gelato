using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using MediaBrowser.Common.Configuration;

namespace Gelato;

public class UIInjectionStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                // Register our middleware early in the pipeline
                builder.UseMiddleware<UIInjectionMiddleware>();
                next(builder);
            };
        }
    }

/// <summary>
/// Middleware to intercept index.html requests and inject Gelato UI scripts.
/// This runs before StaticFileMiddleware, ensuring our version is served.
/// </summary>
public class UIInjectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IApplicationPaths _appPaths;

        public UIInjectionMiddleware(RequestDelegate next, IApplicationPaths appPaths)
        {
            _next = next;
            _appPaths = appPaths;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Check if request is for the web interface index
            var path = context.Request.Path.Value;
            bool isIndexRequest = path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase) ||
                                  path.Equals("/web/", StringComparison.OrdinalIgnoreCase);

            // If it is an index request AND injection is enabled
            if (isIndexRequest)
            {
                try
                {
                    var webPath = _appPaths.WebPath;
                    var indexFile = Path.Combine(webPath, "index.html");

                    if (File.Exists(indexFile))
                    {
                        // value-add: Inject scripts in-memory
                        var originalHtml = await File.ReadAllTextAsync(indexFile);
                        
                        // Prevent double injection
                        if (!originalHtml.Contains("<!-- Gelato Injected -->"))
                        {
                            var modifiedHtml = InjectGelatoUI(originalHtml);

                            context.Response.ContentType = "text/html; charset=utf-8";
                            context.Response.StatusCode = 200;
                            await context.Response.WriteAsync(modifiedHtml, Encoding.UTF8);
                            return; // Short-circuit: do not call _next
                        }
                    }
                }
                catch (Exception ex)
                {
                  

                    try { Console.WriteLine($"UIInjectionMiddleware error: {ex.Message}"); } catch {}

                }
            }

            // Not an index request or disabled or error -> continue pipeline
            await _next(context);
        }

        private string InjectGelatoUI(string html)
        {
            var injection = GenerateInjectionContent();

            if (html.Contains("</body>"))
                return html.Replace("</body>", injection + "\n</body>");
            
            if (html.Contains("</head>"))
                return html.Replace("</head>", injection + "\n</head>");

            return html + injection;
        }

        private string GenerateInjectionContent()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n<!-- Gelato Injected -->");
            
            

            var scripts = new[]
            {
                "gelato.js"
            };

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var asmName = asm.GetName().Name;

            // JS Injection
            foreach (var script in scripts)
            {
                try
                {
                    var resourceName = $"{asmName}.Static.{script}";
                    using var stream = asm.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        var content = reader.ReadToEnd();
                        sb.AppendLine($"<script id=\"gelato-{script}\">");
                        sb.AppendLine(content);
                        sb.AppendLine("</script>");
                    }
                }
                catch (Exception ex) { try { Console.WriteLine($"Error reading resource {script}: {ex.Message}"); } catch {} }
            }

            // CSS Injection
            try
            {
                var cssResource = $"{asmName}.Static.gelato.css";
                using var stream = asm.GetManifestResourceStream(cssResource);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    sb.AppendLine("<style id=\"gelato-custom-css\">");
                    sb.AppendLine(reader.ReadToEnd());
                    sb.AppendLine("</style>");
                }
            }
            catch (Exception ex) { try { Console.WriteLine($"Error reading CSS: {ex.Message}"); } catch {} }

            return sb.ToString();
        }
    }