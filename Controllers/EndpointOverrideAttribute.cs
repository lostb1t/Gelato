using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Gelato.Controllers
{

    /// <summary>
    /// This endpoint method overrides the given endpoint method
    /// Credits to: https://github.com/stenlan/JellyGuard
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public class EndpointOverrideAttribute<TControllerClass> : HttpMethodAttribute
        where TControllerClass : class
    {
        public EndpointOverrideAttribute(string methodName) : base([])
        {
            var targetMethod = typeof(TControllerClass).GetMethod(methodName)
                ?? throw new ArgumentException($"Method ${methodName} not found on ${typeof(TControllerClass).GetType().FullName}.");

            this.Order = -1;

            var methodAttr = (HttpMethodAttribute)(GetCustomAttribute(targetMethod, typeof(HttpMethodAttribute), false)
                ?? throw new ArgumentException($"{targetMethod.DeclaringType?.FullName}#{methodName} does not implement HttpMethodAttribute."));

            ((List<string>)HttpMethods).AddRange(methodAttr.HttpMethods);

            var order = methodAttr.Order;

            RouteAttribute? parentClassAttr = targetMethod.DeclaringType != null ? (RouteAttribute?)GetCustomAttribute(targetMethod.DeclaringType, typeof(RouteAttribute), false) : null;
            var parentClassTemplate = parentClassAttr?.Template;

            order = Math.Min(order, parentClassAttr?.Order ?? 0);

            var templateField = typeof(HttpMethodAttribute).GetField(GetBackingFieldName(nameof(Template)), BindingFlags.Instance | BindingFlags.NonPublic)!;
            templateField.SetValue(this, (parentClassTemplate ?? "") + ((parentClassTemplate != null && methodAttr.Template != null) ? "/" : "") + (methodAttr.Template ?? ""));

            this.Order = order - 1;
        }

        private static string GetBackingFieldName(string propertyName)
        {
            return string.Format("<{0}>k__BackingField", propertyName);
        }
    }
}