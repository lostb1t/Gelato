using Gelato.Config;

namespace Gelato.Services;

public class CatalogService(GelatoStremioProviderFactory stremioFactory)
{
    public async Task<List<CatalogConfig>> GetCatalogsAsync(Guid userId)
    {
        var config = GelatoPlugin.Instance!.Configuration;
        // A new install has no addon yet: nothing to list, and no error to report for it.
        if (string.IsNullOrWhiteSpace(config.GetEffectiveConfig(userId).Url))
            return [];

        var provider = stremioFactory.Create(userId);
        var manifest = await provider.GetManifestAsync();

        if (manifest?.Catalogs == null)
        {
            return config.Catalogs;
        }

        List<CatalogConfig> catalogs = [];

        // Merge manifest catalogs with local config
        foreach (var mCatalog in manifest.Catalogs)
        {
            if (!mCatalog.IsImportable())
                continue;

            var existing = config.Catalogs.FirstOrDefault(c =>
                c.Id == mCatalog.Id && c.Type == mCatalog.Type
            );
            if (existing == null)
            {
                existing = new CatalogConfig
                {
                    Id = mCatalog.Id,
                    Type = mCatalog.Type,
                    Name = mCatalog.Name,
                    Enabled = false,
                    MaxItems = 0, // max items to be imported from this catalog
                    CreateCollection = false,
                    Url = "",
                };
            }
            else
            {
                // Update basic info from manifest just in case
                existing.Name = mCatalog.Name;
            }
            catalogs.Add(existing);
        }
        // A catalog the addon does not list right now keeps its settings: an addon that is being
        // reconfigured, or a list that is gone for a while, must not cost the catalog its library,
        // which is where its items are. It is not listed, and so neither shown nor imported, until
        // the addon has it again. One without settings has nothing to keep.
        var unlisted = config.Catalogs.Where(c =>
            !catalogs.Any(m => m.Id == c.Id && m.Type == c.Type)
            && (c.Enabled || c.CreateCollection || !string.IsNullOrWhiteSpace(c.Path))
        );
        config.Catalogs = [.. catalogs, .. unlisted];

        // Save if we added new ones (optional, but good for persistence)
        GelatoPlugin.Instance.SaveConfiguration();

        return catalogs;
    }

    public void UpdateCatalogConfig(CatalogConfig updatedConfig)
    {
        var config = GelatoPlugin.Instance!.Configuration;
        var existing = config.Catalogs.FirstOrDefault(c =>
            c.Id == updatedConfig.Id && c.Type == updatedConfig.Type
        );

        if (existing != null)
        {
            existing.Enabled = updatedConfig.Enabled;
            existing.MaxItems = updatedConfig.MaxItems;
            existing.CreateCollection = updatedConfig.CreateCollection;
            existing.Path = (updatedConfig.Path ?? "").Trim();
        }
        else
        {
            updatedConfig.Path = (updatedConfig.Path ?? "").Trim();
            config.Catalogs.Add(updatedConfig);
        }

        GelatoPlugin.Instance.SaveConfiguration();
    }

    public CatalogConfig? GetCatalogConfig(string id, string type)
    {
        return GelatoPlugin.Instance!.Configuration.Catalogs.FirstOrDefault(c =>
            c.Id == id && c.Type == type
        );
    }
}
