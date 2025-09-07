# Gelato

Bring the power of Stremio addons directly into Jellyfin. This plugin replaces Jellyfin’s default search with Stremio-powered results, seamlessly injecting them into your Jellyfin library and database.

### Features
- **Unified Search** – Jellyfin search now pulls results from Stremio addons
- **Realtime Streaming** – Streams are resolved on demand and play instantly
- **Database Integration** – Stremio items appear like native Jellyfin items
- **More Content, Less Hassle** – Expand Jellyfin with community-driven Stremio catalogs

### Usage

1. Install the plugin: https://raw.githubusercontent.com/lostb1t/Gelato/refs/heads/gh-pages/repository.json
2. Configure plugin by going to Plugins -> Gelato.  
ONLY AIOSTREAMS IS SUPPORTED. You can create an an manifest through a public instance like: https://aiostreams.elfhosted.com/stremio/configure
3. Add configured base paths to the library of your choice.
4. Search for something thats not in your library. Select result.
Profit!

### Notes:

- only supports aiostreams
- no support for p2p as of yet

### Todo:

- [x] Replace search
- [x] Inject selected search items into the library
- [ ] Replace search images
- [x] Use stremio streams as media sources
- [ ] Enable deletion of stremio media items
- [ ] Add support for subtitle addons
- [ ] Import media from stremio catalogs (scheduled task)
- [ ] Create collections from stremio catalogs (scheduled task)
- [ ] Add more settings like cache ttl and stream name options etc
- [ ] Add proxy option. Maybe.. low prio

### FAQ

- You need to restart the server after editing the manifest/config in aiostreams.
- You should have at least one search enabled catalog. I suggest the tmdb addon.
- I suggest lowering the default timeout on your stremio addons in aiostreams (5 seconds for example)
- stream cache is 3600 min. Can be cleared by restarting the server
- bump up probing and analyse to your liking
  JELLYFIN_FFmpeg__probesize="50M" JELLYFIN_FFmpeg__analyzeduration="5M"