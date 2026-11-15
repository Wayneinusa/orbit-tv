# Superior IPTV Player for Windows

An IPTV player for Windows 10 and Windows 11 (64-bit). It is a player only: it contains no channels, and you add the playlist your own TV provider gives you.

## Features

| Area | What it does |
| --- | --- |
| Playlists | Xtream Codes login, M3U web address or an .m3u file on the PC; several playlists; automatic reload |
| Live TV | Groups on the left; preview with programme details on top; below it the channels with their programme timeline, so the guide is on the same screen. Favourites, recent channels, hidden groups |
| Player | VLC engine, automatic reconnect when a channel freezes, audio and subtitle tracks, picture shape, channel list overlay. The mouse pointer hides after three seconds. A Stats button shows resolution, frame rate, bitrate and codec in small print in the bottom right corner |
| Playback speed | 0.5× to 16× (including 2×, 4×, 8×, 10×) for movies, episodes, catch-up, recordings and downloaded files |
| TV guide | XMLTV (plain or gzip), timeline grid, programme details, search. Each playlist has a primary guide and an optional additional guide; the additional one is used for channels the primary has no schedule for. Right-click a channel and choose Guide source to pick the source for that one channel |
| Catch-up | Xtream timeshift, shift, append, template and Flussonic styles |
| Movies and series | Categories, posters, details, resume where you stopped |
| Downloads | Download a movie or episode from its details, from the player while watching, or tick several and download them together. A Downloads screen shows progress with pause, resume and stop. Interrupted downloads resume by themselves, also after the app is restarted. |
| Recording | Record now, record a programme, schedule from the guide, or choose your own start and end time (Custom time). Reconnects by itself whenever the stream stops. Converted to MP4 automatically or on demand. Any folder, including a network drive. |
| Multi-view | Two or four channels at once |
| Search | Channels, movies, series and the guide in one box. A channel result has an Open category button that shows its category in Live TV |
| Clock | Times are shown in 12-hour format (4:36 PM); 24-hour can be chosen in Settings |

## Keyboard in the player

| Key | Action |
| --- | --- |
| Up / Down | Next / previous channel (live TV) |
| Left / Right | Channel list (live TV), or skip 10 seconds back / forward |
| Enter | Show or hide the controls |
| Space | Pause / play (not for live TV) |
| ] and [ | Faster / slower. \ returns to normal speed |
| M | Mute |
| F or F11, double-click | Full screen |
| Mouse wheel | Volume |
| Esc | Back |

## Installing

Run `SuperiorIPTVPlayer-Setup.exe` (from the repository's Releases page). It installs for the current user and needs no administrator password. The installer is not code-signed, so Windows SmartScreen may show "Windows protected your PC"; choose **More info**, then **Run anyway**.

## Building

The build runs on GitHub (see `.github/workflows/windows.yml`): unit tests, compile, installer, then a self-test on a Windows machine that drives the real app against a pretend TV provider. To build on a PC with the .NET 10 SDK and NSIS 3:

```
dotnet test tests/Core.Tests/Core.Tests.csproj -c Release
dotnet publish src/App/App.csproj -c Release -o publish
makensis installer/setup.nsi
```

## Layout

| Path | Contents |
| --- | --- |
| `src/Core` | Playlists, guide, catch-up, recorder, download manager. No user interface. |
| `src/App` | The Windows app (WinForms) and the VLC player |
| `tests/Core.Tests` | Unit tests, including downloads and recordings whose connection is cut on purpose |
| `samples` | Small test clips used by the app's self-test on the build server |
| `installer` | NSIS installer script |

Video playback uses libVLC (LGPL) through LibVLCSharp.
