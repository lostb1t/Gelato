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

        List<CatalogConfig> catalogs = new List<CatalogConfig>();
        
        // Merge manifest catalogs with local config
        foreach (var mCatalog in manifest.Catalogs) {
            if (mCatalog.IsSearchCapable()) continue; // Skip search catalogs

            var existing = config.Catalogs.FirstOrDefault(c => c.Id == mCatalog.Id && c.Type == mCatalog.Type);
            if (existing == null) {
                existing = new CatalogConfig {
                    Id = mCatalog.Id,
                    Type = mCatalog.Type,
                    Name = mCatalog.Name,
                    Enabled = false,
                    MaxItems = 0, // 0 = use global CatalogMaxItems
                    CreateCollection = false,
                    Url = "" 
                };
                
            } else {
                // Update basic info from manifest just in case
                existing.Name = mCatalog.Name; 
            }
            catalogs.Add(existing);
            
        }
        config.Catalogs = catalogs;

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
            existing.CreateCollection = updatedConfig.CreateCollection;
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
