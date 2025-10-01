<div align="center">
   <img width="125" src="logo.png" alt="Logo">
</div>

<div align="center">
  <h1><b>Gelato</b></h1>
  <p><i>Jellyfin Stremio Integration Plugin</i></p>
</div>

Bring the power of Stremio addons directly into Jellyfin. This plugin replaces Jellyfin’s default search with Stremio-powered results and can automatically import entire catalogs into your library through scheduled tasks — seamlessly injecting them into Jellyfin’s database so they behave like native items.

  <a href="https://discord.gg/t8mt5xbUk">
    <img src="https://img.shields.io/badge/Talk%20on-Discord-brightgreen">
  </a>

### Features
- **Unified Search** – Jellyfin search now pulls results from Stremio addons
- **Catalogs** – Import items from stremio catalogs into your library with scheduled tasks
- **Realtime Streaming** – Streams are resolved on demand and play instantly
- **Database Integration** – Stremio items appear like native Jellyfin items
- **More Content, Less Hassle** – Expand Jellyfin with community-driven Stremio catalogs

NOTICE: ONLY SUPPORTS 10.11

## Usage

1. Install the plugin:
   `https://raw.githubusercontent.com/lostb1t/Gelato/refs/heads/gh-pages/repository.json`

2. Configure the plugin under **Plugins → Gelato**.
   **Note:** Only **AIOStreams** is supported. AIOStreams bundles all your favorite addons into one.
   You can create a manifest via a public instance, for example:
   `https://aiostreams.elfhosted.com/stremio/configure`

3. Add the configured base paths to the Jellyfin library of your choice.

4. Search for a title not already in your library, select a result, and start streaming.

5. Run or schedule the catalog import tasks.

## Notes

- Only **AIOStreams** is supported
- **P2P streams are not yet supported**

---

## Roadmap

- [x] Replace search
- [x] Inject selected search results into the library
- [x] Use Stremio streams as media sources
- [x] Support search result images
- [x] Import media from Stremio catalogs (scheduled task)
- [x] Mixed library support (local files and streams)
- [x] Add support for subtitle addons
- [x] Enable deletion of Stremio media items
- [x] Enable downloads of Stremio media items
- [ ] Create collections from Stremio catalogs (scheduled task)
- [ ] Add more settings (e.g. cache TTL, stream naming options)

### FAQ

- You need to restart the server after editing the manifest/config in aiostreams.
- You should have at least one search enabled catalog. I suggest the tmdb addon.
- I suggest lowering the default timeout on your stremio addons in aiostreams (5 seconds for example)
- debridio tmdb and debridio tvdb are pronlematic. I suggest using the regular tmdb addon.
- stream cache is 3600 min. Can be cleared by restarting the server
- bump up probing and analyse to your liking
  JELLYFIN_FFmpeg__probesize="50M" JELLYFIN_FFmpeg__analyzeduration="5M"