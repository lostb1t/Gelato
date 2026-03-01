namespace Gelato.Config
{
    internal static class ConfigurationHelper
    {
        public static CatalogConfig? GetCatalogConfig(string id, string type)
        {
            return GelatoPlugin.Instance!.Configuration.Catalogs.FirstOrDefault(c =>
                c.Id == id && c.Type == type
            );
        }

        public static PluginConfiguration GetConfig(Guid? userId = null)
        {
            return GelatoPlugin.Instance!.GetConfig(userId ?? Guid.Empty);
        }
    }
}
