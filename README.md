> [!NOTE]
> Looking for the next evolution of Gelato?
> Check out [Remux](https://github.com/lostb1t/remux), a media server written in Rust that replaces Jellyfin instead of plugging into it. It supports local libraries, remote sources, and Stremio addons, and it works with your Jellyfin clients.

<div align="center">
   <img width="125" src="logo.png" alt="Logo">
</div>

<div align="center">
  <h1><b>Gelato</b></h1>
  <p><i>Jellyfin Stremio Integration Plugin</i></p>
</div>

Gelato brings your AIOStreams addon into Jellyfin. Jellyfin's search also finds what the addon offers, and the catalogs you pick are imported into your library on a schedule. Their titles behave like your own media: you browse, search, and play them in any Jellyfin client.

<a href="https://discord.gg/rEbhk4RBhs">
    <img src="https://img.shields.io/badge/Talk%20on-Discord-brightgreen">
</a>

### Features
- **Search**: Jellyfin's search also finds titles from the addon, ready to play.
- **Catalogs**: import the addon's catalogs into your library, kept up to date by a scheduled task.
- **Streams on demand**: Gelato finds the streams when you press play. Nothing is downloaded.
- **Like your own media**: the titles are regular Jellyfin library items.
- **Proxy**: streams go through Jellyfin, so your debrid service sees a single IP.
- **Per-user settings**: a user can have their own addon, for example for a kids' account.
- **Seerr requests**: a request approved in Seerr (Jellyseerr, Overseerr) adds the title to your library.

## Usage

1. Set up AIOStreams. Host it yourself or use a public instance, for example the [ElfHosted public instance](https://aiostreams.elfhosted.com/stremio/configure).
   
   New to debrid? Sign up with one of my <a href="https://github.com/lostb1t/Gelato?tab=readme-ov-file#support-me">referrals</a>.
   
   You need at least the **TMDB addon** turned on for search, and one addon that finds streams (Comet, for example).
   Or import the [starter config](aiostreams-config.json), then turn on your debrid providers under **Services**.

2. On Jellyfin 12, add `https://raw.githubusercontent.com/lostb1t/Gelato/refs/heads/gh-pages/repository.json` to your plugin repositories.

   **Upgrading from Jellyfin 10.11?** First update Gelato to its final 10.11 release, and shut the server down normally at least once before you upgrade Jellyfin. That release empties the Gelato folders on shutdown; without it, the first Jellyfin 12 start deletes every Gelato title. Its settings page shows whether your server is ready. If you upgraded without it, Gelato runs the **Repair watch state** task once on the first start and brings back every title that still has watch state (see the FAQ).

3. Install the plugin and open its settings. The **Setup assistant** walks you through the addon, the libraries, and the catalogs.
   Gelato supports only **AIOStreams**.

   To change the libraries later, use **Default movies library** and **Default series library** on the **Libraries** tab. Pick a library you have and save, and Gelato adds a folder of its own to it. Or pick **New library**: Gelato creates the folder, and **Copy folder** gives you its path for Jellyfin's library dialog.

4. In Jellyfin, check each library's settings: **Gelato** should be first under the metadata downloaders and image fetchers. Gelato doesn't change the settings of a library you had before, so turn it on there yourself.

5. Search for a movie and play it. Or run the **Import Catalogs** task to fill your library.

For a step-by-step guide, see the [setup guide](https://github.com/lostb1t/Gelato/discussions/40).

## Seerr requests

Gelato adds titles requested in Seerr (Jellyseerr, Overseerr). The Gelato settings show the fields below on the **Libraries** tab, each with a **Copy** button. In Seerr, go to **Settings > Notifications > Webhook**:

- **Webhook URL:** `http://<your-jellyfin>:8096/gelato/seerr`
- **Authorization Header:** `MediaBrowser Token="<API key>"`, with a key from Jellyfin's **Dashboard > API Keys**
- **JSON Payload:** leave the default
- **Notification Types:** Request Approved and Request Automatically Approved

An approved request adds the movie or series to your library, and Seerr marks it available at its next Jellyfin scan. If the requester has libraries of their own on Gelato's **Users** tab, the title goes there. A series request adds the whole series, also when it named only some seasons.

## Notes

- Gelato supports only **AIOStreams**.

### FAQ

- After you change your config in AIOStreams, save it there, then select **Refresh** on the **Catalogs** tab of the Gelato settings. Gelato reads the addon again; you don't need to restart.
- Turn on at least 1 catalog that supports search in AIOStreams. I suggest the TMDB addon.
- To start over, run the **WARNING: purge all gelato items** task in **Dashboard > Scheduled Tasks**. It removes the watch state along with the titles, so it's a real fresh start.
- Removing a title doesn't lose its watch state. Jellyfin keeps it, and Gelato puts it back when the title returns, so a movie you delete and add again later still has your progress.
- If titles went missing and took your watch state with them (after a Jellyfin 12 upgrade, for example), run the **Repair watch state** task in **Dashboard > Scheduled Tasks**. It imports what's gone again and gives it back its watch state. It has no schedule: it can't tell what you deleted on purpose from what you lost by accident, so it runs only when you start it, and it also brings back titles you deleted yourself. Gelato also runs it once on the first start after you install this release, and after every later major Jellyfin upgrade. Don't run Jellyfin's own **User data cleanup task** first: that task deletes the kept watch state.
- I suggest a lower timeout for your Stremio addons in AIOStreams, for example 5 seconds.
- Debridio TMDB and Debridio TVDB cause problems. I suggest the regular TMDB addon.
- To clear the stream cache, restart the server.
- A catalog can import into a library of its own, for example an Anime or Documentaries library: use the **Library** column on the **Catalogs** tab. Pick a library you have and save, and Gelato adds a folder of its own to it. For a new one, pick **New library**: Gelato creates the folder, and you add a library with it in Jellyfin (Jellyfin needs a folder for a new library). At the next import, new titles go there, and the titles the catalog imported before move there with their watch state. A title that 2 catalogs list stays with the one that imported it first. A movies library takes only the catalog's movies, and a shows library only its series; the rest goes to the default library. Set the catalog back to **Default** to move its titles back. A search inside such a library also asks the addon, and a result you open from it goes into that library.
- Opening a search result adds it to your library. With **Keep opened search results out of the library until played** on (on the **Search** tab), it stays out of the library, the home screen rows, and the counts until someone plays it, marks it as a favorite, likes it (a watchlist), or adds it to a collection or playlist. Search still finds it. If nobody does within 5 days, Gelato removes it again.

### ❤️ Support the Project

- ⭐ **[Star the repository](https://github.com/lostb1t/Gelato)** on GitHub.
- 🤝 **Contribute**: Report issues, suggest features, or submit pull requests.
- ☕ **Donate**:
  - **[Ko-fi](https://ko-fi.com/lostb1t)**
