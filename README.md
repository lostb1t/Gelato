> [!NOTE]
> Looking for the next evolution of Gelato?
> Check out [Remux](https://github.com/lostb1t/remux). A Rust-based media server designed as a full replacement for Jellyfin rather than a plugin. It supports local libraries, remote sources, Stremio addons, and works with existing Jellyfin clients.

<div align="center">
   <img width="125" src="logo.png" alt="Logo">
</div>

<div align="center">
  <h1><b>Gelato</b></h1>
  <p><i>Jellyfin Stremio Integration Plugin</i></p>
</div>

Bring the power of Stremio addons directly into Jellyfin. This plugin replaces Jellyfin’s default search with Stremio-powered results and can automatically import entire catalogs into your library through scheduled tasks, seamlessly injecting them into Jellyfin’s database so they behave like native items.

<a href="https://discord.gg/rEbhk4RBhs">
    <img src="https://img.shields.io/badge/Talk%20on-Discord-brightgreen">
</a>

### Features
- **Unified Search** – Jellyfin search now pulls results from Stremio addons
- **Catalogs** – Import items from stremio catalogs into your library with scheduled tasks
- **Realtime Streaming** – Streams are resolved on demand and play instantly
- **Database Integration** – Stremio items appear like native Jellyfin items
- **Act as a proxy** - Streams are proxied through Jellyfin, so debrid sees everything as a single IP.
- **Per user settings** - Users can have their own manifest, perfect for age restricted accounts.
- **More Content, Less Hassle** – Expand Jellyfin with community-driven Stremio catalogs

## Usage

1. Set up an AIOStreams manifest. You can self-host or use a public instance, for example: [Elfhosted public instance](https://aiostreams.elfhosted.com/stremio/configure)
   
   If you are new to debrid and are signing up please use one of my <a href="https://github.com/lostb1t/Gelato?tab=readme-ov-file#support-me">referrals</a>.
   
   At minimum, you need the **tmdb addon enabled** for search and one addon that provides streams (comet for example).
   Alternatively, you can import the [starter config](aiostreams-config.json). Remember to enable your debrid providers under services after importing the config.

2. Make sure you are running Jellyfin 12 and add `https://raw.githubusercontent.com/lostb1t/Gelato/refs/heads/gh-pages/repository.json` to your plugin repositories.

   **Upgrading from Jellyfin 10.11?** Update Gelato to the final 10.11 release first and shut the server down normally at least once before upgrading Jellyfin. The first Jellyfin 12 start deletes every Gelato item unless that release has emptied the Gelato library folders on shutdown, and its settings page shows whether the install is ready. If you upgraded without it, Gelato runs the repair watch state task once on the first start and recovers whatever still has watch state (see the FAQ).

3. Install and configure the plugin.
   **Note:** Only **AIOStreams** is supported.

4. Create a movies and a shows library in Jellyfin (they can be empty) and pick them as the default libraries in the Gelato settings. Gelato adds a folder of its own to each and starts a library scan.
   4.5 For shows, enable the "Gelato missing season/episode fetcher" and put it on top of the metadata downloaders.

5. Profit! Now search for your favorite movie and start streaming. Or run the catalog import task to populate your db.

For a more in depth guide see [starter guide](https://github.com/lostb1t/Gelato/discussions/40)

## Notes

- Only **AIOStreams** is supported

### FAQ

- You need to restart the server after editing the manifest/config in aiostreams.
- You should have at least one search enabled catalog. I suggest the tmdb addon.
- If something borked or you want to start over, you can use the purge task under scheduled tasks. It clears watch state along with the items, so it really is a fresh start.
- Watch state is not lost when items are removed. Jellyfin parks it and Gelato puts it back when the item returns, so a film you delete and later re-add still has your progress on it.
- If items went missing and took your watch state with them (after a Jellyfin 12 upgrade, say), run the **repair watch state** task under scheduled tasks. It re-imports what is gone and reattaches the watch state. It has no schedule: it cannot tell what you deleted on purpose from what you lost by accident, so it only runs when you start it, and it will bring back things you deleted yourself. Gelato also runs it once by itself on the first start after this release is installed and after every later Jellyfin major upgrade. Do not run Jellyfin's own "clean up user data" task first, that is what actually deletes parked watch state.
- I suggest lowering the default timeout on your stremio addons in aiostreams (5 seconds for example)
- debridio tmdb and debridio tvdb are pronlematic. I suggest using the regular tmdb addon.
- Stream cache can be cleared by restarting the server
- A catalog can import into a library of its own (the Library column under Catalogs), an Anime or Documentaries library say. Create the library in Jellyfin (it can be empty), pick it and save: Gelato adds a folder of its own to it. On the next import new items go there, and items the catalog imported before move there, watch state included. An item two catalogs list stays with the one that took it first. A movies library only takes the catalog's movies and a shows library its series; the other kind goes to the default library. Setting the catalog back to Default moves its items back. A search inside such a library is answered by the addon too, and a result opened from it goes into that library.

### ❤️ Support the Project

- ⭐ **[Star the repository](https://github.com/lostb1t/Gelato)** on GitHub.
- 🤝 **Contribute**: Report issues, suggest features, or submit pull requests.
- ☕ **Donate**:
  - **[Ko-fi](https://ko-fi.com/lostb1t)**
