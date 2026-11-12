# Superior IPTV Player for iPad and Apple TV

An IPTV player for iPad and Apple TV, written in SwiftUI with one shared codebase. Its layout and feature set follow the approach of TiviMate on Android (group list, channel list with now/next, preview pane, timeline guide, catch-up, recording, multi-view) under its own name, icon and artwork.

Superior IPTV Player is a player only. It contains no channels; you add the playlist your own TV provider gives you.

## Status

Both apps compile on GitHub's Mac servers and the engine's unit tests pass. Neither app has been run on a real iPad or Apple TV yet, so playback, recording and MP4 conversion on the devices themselves are still untested. The remaining uncertainties are listed under "Known risks" at the end.

The Windows version lives in the `windows/` folder and has its own README there.

## What is included

| Area | What it does |
| --- | --- |
| Playlists | M3U address or Xtream Codes login, several playlists at once, automatic reload, custom User-Agent |
| Live TV | Groups, channel list with current programme and progress, preview pane, favorites, recent channels, hidden groups |
| Player | VLC engine (plays MPEG-TS and HLS), automatic reconnect when a channel freezes, audio and subtitle tracks, aspect ratio, channel list overlay, channel up/down |
| TV guide | XMLTV (plain or gzip), timeline grid, programme details, guide search |
| Catch-up | Xtream timeshift, `catchup="shift"`, `append`, `default` templates and Flussonic |
| Movies and series | Categories, posters, details, seasons and episodes, resume where you stopped |
| Recording | Record now, record a programme, schedule from the guide, automatic resume, MP4 conversion |
| Multi-view | Two or four channels at once, sound follows the selected tile |
| Search | Channels, movies, series and upcoming programmes in one box |

### How recording works

A recording is captured as `.ts`. That format is used on purpose: it is the only common container that stays playable when a file is cut and joined at arbitrary points, which is exactly what a reconnect does.

If the stream stops for any reason (server hiccup, Wi-Fi drop, timeout, an error from the provider), the recorder reconnects by itself and appends to the same file. It retries after 1, 2, 4 and 8 seconds and then every 15 seconds, and it only gives up when the end time is reached, you press Stop, or storage runs out. If the app itself was closed or suspended in the middle, the recording picks up again as soon as the app is open, for as long as the end time has not passed.

When a recording finishes it is converted to `.mp4` without re-encoding, so there is no quality loss. This is on by default (Settings › Recording). With it off, each finished recording has a "Convert to MP4" action in the Recordings screen. If a conversion fails, the `.ts` file is kept and still plays.

On iPad, recordings appear in the Files app under On My iPad › Superior IPTV › Recordings.

## Step 1: get the app files

The apps are built on GitHub's Mac servers. Open the repository's **Actions** tab, choose the latest successful **Build** run, and download `OrbitTV-iPad-unsigned` and `OrbitTV-AppleTV-unsigned` from **Artifacts** (you must be signed in to GitHub). Each is a zip containing an `.ipa` file. The files keep their original project name; the installed app is called Superior IPTV Player.

The source is uploaded as one bundle, `OrbitTV-source.tar.gz`. Uploading a new copy of that file starts a build, and the build unpacks it into the repository so the code can be browsed.

## Step 2: install on your devices

Apple requires every app to be signed before a device will run it. There are two routes.

### Route A: TestFlight (works for both iPad and Apple TV)

This needs the paid Apple Developer Program (99 USD per year). It is the only practical way to get the app onto an Apple TV from a Windows PC.

1. Enroll at developer.apple.com/programs.
2. At developer.apple.com, under Certificates, Identifiers & Profiles › Identifiers, register an App ID with a bundle identifier of your own, for example `com.yourname.orbittv`.
3. At appstoreconnect.apple.com, create a new app with that bundle identifier and tick both iOS and tvOS as platforms.
4. In App Store Connect under Users and Access › Integrations › App Store Connect API, create a team key with **Admin** access. Note the **Key ID** and **Issuer ID** and download the `.p8` file (it can only be downloaded once).
5. Find your **Team ID** at developer.apple.com under Membership details.
6. In your GitHub repository open Settings › Secrets and variables › Actions and add:
   - Secrets: `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8` (open the `.p8` file in Notepad and paste the whole text), `APPLE_TEAM_ID`
   - Variable (the Variables tab): `BUNDLE_ID` with your bundle identifier
7. In the Actions tab choose **TestFlight › Run workflow**.
8. Install the TestFlight app on the iPad and the Apple TV, sign in with the same Apple ID, and install Superior IPTV Player from there once Apple has finished processing the upload.

TestFlight builds expire after a limited time (90 days at the time of writing). Running the workflow again uploads a fresh one.

### Route B: free sideloading (iPad only, from Windows)

Sideloadly (sideloadly.io) signs an `.ipa` with a free Apple ID and installs it over USB. Use the `.ipa` from Step 1. Apps signed with a free Apple ID stop working after 7 days and must be installed again.

For Apple TV this route does not work from Windows in practice. Sideloadly's own documentation says an Apple TV without a USB port (every Apple TV 4K) can only be paired from a Mac.

## Using the app

Add a playlist on first launch: either an Xtream Codes login (server, username, password) or an M3U address. The guide is taken from the playlist automatically when it advertises one; otherwise enter an XMLTV address in the playlist's settings.

**Apple TV remote while watching live TV:** up and down change channel, left opens the channel list, select shows the controls, Menu/Back steps out. During films, left and right skip 10 seconds and Play/Pause works as usual. Press and hold a channel in any list for Favorite and Record.

**iPad while watching:** tap for the controls, swipe up or down to change channel, swipe right for the channel list. Touch and hold a channel in a list for Favorite and Record.

## Limits that come from Apple, not from the app

- **Recording only runs while the app is open.** iPadOS and tvOS suspend apps shortly after they leave the screen. A scheduled recording starts on time only if Superior IPTV Player is open; a recording that was interrupted this way resumes when you come back. The screen is kept awake while a recording is running.
- **Apple TV has no permanent app storage.** Recordings on Apple TV are kept in the app's cache, which tvOS may clear when it runs low on space, and there is no way to copy them off the device. For recordings you want to keep, use the iPad.
- **Each multi-view tile uses one connection** from your provider. Many subscriptions allow only one or two at a time.

## Differences from TiviMate

This is a first version. It does not include TiviMate's settings backup and restore, parental-control PIN, channel reordering and renaming, remote-control button mapping, recurring (series) recording rules, or picture-in-picture.

## Changing the name

The display name is `CFBundleDisplayName` in `Config/iOS-Info.plist` and `Config/tvOS-Info.plist`. The icon artwork is in `App/iOS/Assets.xcassets` and `App/tvOS/Assets.xcassets`.

## Project layout

| Path | Contents |
| --- | --- |
| `Core/` | Playlist, Xtream, guide, catch-up, HLS and recording engine. No user interface. |
| `App/Shared/` | The interface and app logic used by both platforms |
| `App/iOS/`, `App/tvOS/` | Icons for each platform |
| `Config/` | Info.plist for each platform |
| `Tests/` | Unit tests for the engine (`swift test`) |
| `project.yml` | Project definition for XcodeGen; the Xcode project is generated from it |
| `.github/workflows/` | Cloud build and TestFlight upload |

On a Mac: `brew install xcodegen && xcodegen generate`, then open `OrbitTV.xcodeproj`.

## Known risks

- **VLC integration.** The player and the MP4 converter were written against VLCKit 3.6 without access to its header files. Small naming mismatches are the most likely source of compiler errors.
- **MP4 conversion** depends on the MP4 writer being present in the VLCKit build for iOS and tvOS. If it is not, conversion fails cleanly and recordings stay as `.ts`; the fix would be to bundle a different converter.
- **TestFlight workflow.** It relies on Apple's cloud signing and has not been run. Signing setups often need one or two adjustments on first use.
- **Very large guides** (several thousand channels with a week of listings) use a lot of memory on Apple TV. Hiding groups does not reduce this; lowering "Days ahead" in Settings does.
- **App Store.** If you later publish it, expect questions from App Review about IPTV players, and the VLC engine's LGPL licence requires an attribution notice in the app.
