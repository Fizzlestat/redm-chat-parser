# RedM Chat Parser

This application captures RedM chat (old and new) and parses it. Much like the GTA: World Chat Parser, logs are saved periodically and/or manually and use the same format, which means you can keep this parser's logs in that same folder layout.

This is a windows application that requires .NET 8 Desktop Runtime (x64).

## WARNING:

Given that this is an unsigned application, you will get a Windows SmartScreen warning the first time you run it. The application is open-source and the code is available for you to compile if you wish to do so.

## Live transcript

The main panel is the live transcript.

- Chat appears as it comes in, using the colors from the HUD.
- Lines with no HUD color still get a readable color: private messages, local out-of-character (`/b`), annotated emotes, roleplay actions, and in-character speech.
- A keyword match highlights the whole line.
- Drag to select text. Copy with Ctrl+C, or use the right-click menu to copy or select all.
- Links that start with `http://`, `https://`, or `www.` are underlined and open in the browser. Punctuation after a link stays as normal text.
- The view stays at the bottom. If you scroll up to read, it stops following until you return to the bottom.
- If the parser starts while RedM is already open, chat already on screen is picked up as soon as the HUD attaches. Older lines that were only in the session log before that are left alone.
- The status next to the title reports what the parser is doing: starting, waiting for RedM, waiting for chat, looking for chat, capturing (with a line count), saving after the game closes, or saved and waiting.

## Saving logs

Logs are plain text. The default folder is `Documents\RedM Chat Logs`. You can point it at the folder that already holds your RedM logs.

Files are saved as:

```text
Year\MON\dd.MON.yyyy-HH.mm.ss.txt
```

Each file starts with a date and time header, then the chat lines. An existing file is replaced only when the new copy is longer, so a shorter write cannot wipe a longer log.

A log is written:

- Every 5 minutes, when there is something new to save.
- When both RedM and the game have been closed. The parser waits 5 seconds so the last lines are included, then saves.
- When you exit the parser.
- When you choose **Save log now**.
- Immediately, if the parser is opened during a session that was already being recorded.

**Open folder** opens the log folder, or selects the last saved file. After a game close, a desktop notification titled **Log saved** shows the file name. The same notice appears in the window. If the window is minimized or hidden, the taskbar button flashes.

## Keyword notifications

Keywords are matched against new chat. Put one keyword or phrase on each line.

A desktop notification appears only when RedM is not the focused window. The title is **Keyword Notification**. The body is the chat line. Up to three notifications are shown at once.

- Notifications can be turned off.
- Sound can be turned off separately.
- The built-in ping is used unless you choose a `.mp3`, `.wav`, or `.ogg` file. The file must be 2 seconds or shorter. You can preview it, and you can return to the built-in ping. If a saved file cannot be played when an alert fires, the built-in ping is used instead.
- How long a notification stays up is set in seconds, from 1 to 60. The default is 5.
- Matching can be case sensitive, or it can ignore case.
- The entrance is slide in, pop in, or none.

## Window

- **Minimize** sends the window to the taskbar.
- **Minimize to tray** hides it. The tray icon is shown only while the window is hidden. Left-click the icon, or choose **Open**, to bring it back.
- **Close** and **Exit** both ask for confirmation. The current chat is saved before the parser closes.
- Right-click the tray icon for **Open** and **Exit**.
- Only one copy of the parser runs at a time. Starting it again tells you it is already open.

## Appearance

The gold frame and dark background are the defaults. You can change them.

- **Primary** is the frame, highlights, and accents.
- **Secondary** is the window background.
- **Interface font** is the font for the settings and the rest of the window. The default is Segoe UI.
- **Transcript font** is the font for the live chat. The default is Consolas.
- **Reset appearance** restores the default colors and fonts.

## Languages

The interface can be shown in:

- English
- Spanish
- French
- Korean
- Russian
- Romanian
- Portuguese (Brazilian)

## Startup and updates

- **Start with Windows** adds the parser to your Windows startup.
- **Start minimized to tray** is available when startup is on. Windows then opens the parser already hidden.
- **Check for updates on startup** looks for a newer GitHub release when the parser opens. You can also check manually.
- If an update exists, the parser asks before downloading it. On yes, it closes, installs the new version, and opens again.
