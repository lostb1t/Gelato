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
- **Act as an proxy** - Streams are proxied through Jellyfin, so debrid sees everything as a single IP.
- **More Content, Less Hassle** – Expand Jellyfin with community-driven Stremio catalogs

**NOTICE: ONLY SUPPORTS JELLYFIN 10.11**

## Usage
**STEP ONE**
1. Setup an aiostreams manifest. You can selfhost or use an public instance, for example: `https://aiostreams.elfhosted.com/stremio/configure`
   **Note:** Only **AIOStreams** is supported.
      
   In the AIOstreams settings you need to enable 
   a. Add a Debrid provider (input your debrid token.) **Mandatory**
   b. A search addon  (the TMDB addon is recommended)  NB: NOT the TMDB collections addon.
   c. Another addon that provides the actual streams. e.g Comet, Mediafusion etc

   Alternatively you can import the [starter config](aiostreams-config.json). Remember to enable your debrid providers under services after importing the config.
   
   **p2p is not supported at this time**

**STEP TWO**
- Make sure you are running Jellyfin 10.11.
  
  Add `https://raw.githubusercontent.com/lostb1t/Gelato/refs/heads/gh-pages/repository.json` as a source  to your plugin repositories.  Refresh the page

- Install the plugin, **then restart Jellyfin**

**STEP THREE**
- Create new library folders for Movies and Shows on your machine/server. These folders should have permissions that jellyfin can access, usually 1000:1000

- Create new Movie and Shows libraries in jellyfin that point to these empty folders. choose TMDB as metadata downloader for both libraries as TVDB addon may cause wierd behavior.

**STEP 4**
Configure Gelato Plugin

- Input the resulting URL from AIOstreams (which will pop up after savinng the aiostreams config and clicking on the Install button) to the Stremio URL section

- Add the configured paths to the Movie and Shows Jellyfin libraries created for Gelato in Step Three to the Base Path section of the plugin settings of your choice.

Save the plugin settings and then restart Jellyfin. (Mandatory)


5. Optional but recommended. Lower the probe and analyze size by setting the following environment variables: ex:
```
JELLYFIN_FFmpeg__probesize="50M"
JELLYFIN_FFmpeg__analyzeduration="5M"
```

6. Profit! Now search for your favorite movie and start streaming. Or run the catalog import task to populate your db.

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
- if something borked or you want to start over, you can use the purge task under scheduled tasks.
- I suggest lowering the default timeout on your stremio addons in aiostreams (5 seconds for example)
- debridio tmdb and debridio tvdb are pronlematic. I suggest using the regular tmdb addon.
- Stream cache can be cleared by restarting the server
- bump down probing and analyse to your liking for faster playback
  JELLYFIN_FFmpeg__probesize="50M" JELLYFIN_FFmpeg__analyzeduration="5M"

### Support

Want to support me? Use my torbox referral code <a target="_blank"
          href="https://www.torbox.app/subscription?referral=abe1a9d9-53c9-449a-9d85-ab3dfb5d188d">abe1a9d9-53c9-449a-9d85-ab3dfb5d188d</a>
