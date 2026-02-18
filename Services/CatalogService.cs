using Gelato.Configuration;
using Microsoft.Extensions.Logging;
using static Gelato.Configuration.PluginConfiguration;

namespace Gelato.Services;

public class CatalogService {
    private readonly ILogger<CatalogService> _logger;
    private readonly GelatoStremioProviderFactory _stremioFactory;

    public CatalogService(
        ILogger<CatalogService> logger,
        GelatoStremioProviderFactory stremioFactory
    ) {
        _logger = logger;
        _stremioFactory = stremioFactory;
    }

    public async Task<List<CatalogConfig>> GetCatalogsAsync(Guid userId) {
        var config = GelatoPlugin.Instance!.Configuration;
        var provider = _stremioFactory.Create(userId);
        var manifest = await provider.GetManifestAsync();

        if (manifest?.Catalogs == null) {
            return config.Catalogs;
        }

        // Merge manifest catalogs with local config
        foreach (var mCatalog in manifest.Catalogs) {
            if (mCatalog.IsSearchCapable()) continue; // Skip search catalogs

            var existing = config.Catalogs.FirstOrDefault(c => c.Id == mCatalog.Id && c.Type == mCatalog.Type);
            if (existing == null) {
                existing = new CatalogConfig {
                    Id = mCatalog.Id,
                    Type = mCatalog.Type,
                    Name = mCatalog.Name,
                    Enabled = false, // Default to disabled
                    MaxItems = config.CatalogMaxItems > 0 ? config.CatalogMaxItems : 100,
                    Url = "" 
                };
                config.Catalogs.Add(existing);
            } else {
                // Update basic info from manifest just in case
                existing.Name = mCatalog.Name; 
            }
        }

        // Save if we added new ones (optional, but good for persistence)
        GelatoPlugin.Instance.SaveConfiguration();

        return config.Catalogs;
    }

    public void UpdateCatalogConfig(CatalogConfig updatedConfig) {
        var config = GelatoPlugin.Instance!.Configuration;
        var existing = config.Catalogs.FirstOrDefault(c => c.Id == updatedConfig.Id && c.Type == updatedConfig.Type);
        
        if (existing != null) {
            existing.Enabled = updatedConfig.Enabled;
            existing.MaxItems = updatedConfig.MaxItems;
            // Name, Id, Type are generally fetching-only, but we can allow name override if we want.
            // For now, only settings.
        } else {
            config.Catalogs.Add(updatedConfig);
        }
        
        GelatoPlugin.Instance.SaveConfiguration();
    }
    
    public CatalogConfig? GetCatalogConfig(string id, string type) {
        return GelatoPlugin.Instance!.Configuration.Catalogs
            .FirstOrDefault(c => c.Id == id && c.Type == type);
    }
}
