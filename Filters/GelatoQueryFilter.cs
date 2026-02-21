using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Captures the "gelato" query parameter and stores it on HttpContext.Items for downstream usage.
/// </summary>
public sealed class GelatoQueryFilter : IAsyncActionFilter, IOrderedFilter {
    public int Order { get; init; } = 2;
    private const string ItemsKey = "gelato";

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next) {
        try {
            if (!ctx.HttpContext.Items.ContainsKey(ItemsKey)) {
                var val = ctx.HttpContext.Request.Query[ItemsKey];

                if (!string.IsNullOrWhiteSpace(val)) {
                    ctx.HttpContext.Items[ItemsKey] = val.ToString();
                }
            }
        } catch {
            // swallow - this filter must not break request processing
        }

        await next();
    }
}
