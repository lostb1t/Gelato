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

1. Setup an aiostreams manifest. You can selfhost or use an public instance, for example: [Elfhosted public instance](https://aiostreams.elfhosted.com/stremio/configure)
   
   If you are new to debrid and are signing up please use one of my <a href="https://github.com/lostb1t/Gelato?tab=readme-ov-file#support-me">referrals</a>.
   
   At minimum you need the **tmdb addon enabled** for search and one addon that provides streams (comet for example).
   Alternative you can import the [starter config](aiostreams-config.json). Remember to enable your debrid providers under services after importing the config.
   
   **p2p support currently in beta**

2. Make sure you are running Jellyfin 10.11 and add `https://raw.githubusercontent.com/lostb1t/Gelato/refs/heads/gh-pages/repository.json` to your plugin repositories.

3. Install and configure the plugin.
   **Note:** Only **AIOStreams** is supported.

4. Add the configured base paths to the Jellyfin library of your choice. After adding them, start a library scan.

5. Profit! Now search for your favorite movie and start streaming. Or run the catalog import task to populate your db.

For a more in depth guide see [starter guide](https://github.com/lostb1t/Gelato/discussions/40)

## Notes

- Only **AIOStreams** is supported
- **P2P currently in beta**

## Demo :tv:

Here's a [video demo](https://www.youtube.com/watch?v=t_5Guc5YOoM) recorded by our friends at [ElfHosted](https://elfhosted.com). You can trial a [JellyGoblin](https://store.elfhosted.com/product/jellygoblin/) setup from ElfHosted for 7 days (*hosted Jellyfin+AIOStreams, bundled with a TorBox subscription*), for just $1! :heart_eyes_cat:

### FAQ

- You need to restart the server after editing the manifest/config in aiostreams.
- You should have at least one search enabled catalog. I suggest the tmdb addon.
- if something borked or you want to start over, you can use the purge task under scheduled tasks.
- I suggest lowering the default timeout on your stremio addons in aiostreams (5 seconds for example)
- debridio tmdb and debridio tvdb are pronlematic. I suggest using the regular tmdb addon.
- Stream cache can be cleared by restarting the server

### Support me

Want to support me? Use my referral codes

- <a target="_blank"
          href="https://www.torbox.app/subscription?referral=abe1a9d9-53c9-449a-9d85-ab3dfb5d188d">Torbox referral link</a> </br> Or use my referral code: abe1a9d9-53c9-449a-9d85-ab3dfb5d188d
- <a target="_blank"
          href="http://real-debrid.com/?id=10148658">RealDebrid Signup</a> </br>
  Or use my referral code: 10148658
