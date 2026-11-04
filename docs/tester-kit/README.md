# Let's try John's Twitch Handler

Thanks for helping me try this out. I built the Handler to keep stream checks, controls, and highlights in one place. I want to know whether it makes your stream easier, and where it gets in your way.

You do not need to be technical. If you get stuck, stop there and tell me what you were trying to do. That is useful feedback too.

## 1. Get your copy

You need a **64-bit Windows PC**, a browser, and internet access for downloads and account sign-in. Use the same PC that runs OBS. A Twitch account is only needed if you want to try the Twitch features. You do not need to install any programming tools.

1. Open the [download page](https://github.com/ReallyJohnSmith/johns-twitch-handler/releases).
2. Open the release John asked you to test. Under **Assets** (the file list), choose the file ending in **Setup.exe**.
3. Ignore **Source code (zip)** and **Source code (tar.gz)**. Those are for people working on the program, not installing it.
4. If there is no Setup file, or GitHub says you cannot access the page, ask John for a download. Do not try to build it yourself.
5. While you are **not streaming**, open the downloaded Setup file. It opens the app in your browser and adds Desktop and Start Menu shortcuts called **John's Twitch Handler**.

Windows may warn about an unrecognized app because the test installer is not signed. If you are unsure, stop and check with John. Do not turn off your antivirus or other Windows protection.

The browser window is normal: the app runs on your PC. John does not receive your settings or stream reports automatically.

## 2. Connect OBS

OBS is the program you use to broadcast your stream. Leave streaming and recording stopped for this first check.

1. Open **OBS**.
2. At the top, choose **Tools → WebSocket Server Settings**. This lets the Handler talk to OBS on your PC.
3. Turn on **Enable WebSocket server**. Keep **Enable Authentication** turned on too.
4. Choose **Show Connect Info**, then copy the **Server Password**. Keep it private.
5. Return to the Handler. Open **Settings** if the setup window is not already showing.
6. Paste that password into the OBS **Password** box and choose **Connect OBS**.

Leave the advanced address setting alone unless you have changed the OBS port before. If that menu is missing or the connection fails, ask John for help instead of changing firewall or router settings.

## 3. Choose what you want to connect

- To try Twitch, choose **Connect with Twitch**. Twitch opens a sign-in or approval page. Check that you are signing in to your own account, review the permissions, and approve only if you are comfortable. Handle any sign-in checks yourself.
- You can skip Twitch and still try appearance settings and the guided demo.
- Leave Spotify, Tangia, StreamElements, and TikFinity set to **No** unless you use them and want to test them.
- You should not need developer IDs, access tokens, or stream keys. If the normal connection button asks for those, stop and let John know.

Now follow the [short first-test checklist](CHECKLIST.md). You do not need to go live.

## 4. Tell me how it went

A private reply to the message where John invited you is enough. Try these three questions:

1. What confused you?
2. What did not work?
3. What would make you use this regularly?

The [feedback guide](FEEDBACK.md) has a short message you can copy and an optional GitHub form. Please read the [known limitations](KNOWN-LIMITATIONS.md) if a feature seems to be missing.

## Opening it again and getting updates

- Open **John's Twitch Handler** from the Desktop or Start Menu. Do this between streams: the shortcut restarts the app.
- Closing the browser tab does **not** stop the background app. To reopen just the dashboard while the app is running, use [Open my dashboard](http://127.0.0.1:5274/).
- There is no automatic updater yet. When John shares a newer release, open its Setup file while you are not streaming. Updates are designed to keep your saved connections, preferences, and reports.
- If you want to stop testing or remove it, ask John for help. This test installer does not yet include an uninstall button. Deleting the shortcut alone does not remove the app or its saved data.

## If something goes wrong

- **The app does not open:** tell John what appeared. A cropped screenshot of the error is helpful. Do not send whole log folders.
- **OBS will not connect:** check that OBS is open and that you used the Server Password from its WebSocket settings, not your Twitch password.
- **A button or panel is missing:** check the version near the app name. This guide covers .016. Try Advanced mode for the full set of panels in your build.
- **You moved panels and want them back:** if your build has **Reset layout**, read its confirmation first. It resets the arrangement, but keeps your account connections and appearance choices.

Want a quick look first? [Watch or download the one-minute video](https://github.com/ReallyJohnSmith/johns-twitch-handler/raw/refs/heads/main/docs/media/Johns-Twitch-Handler-60-Second-Tour.mp4). It is an MP4, about 6.5 MB, with captions on screen. The video shows features included in .016. Downloading it needs internet access; this guide works without it.
