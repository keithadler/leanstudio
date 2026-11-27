# Lean Studio privacy policy

Lean Studio is an editor that runs on your computer. It has no account, no telemetry and no analytics, and its author runs no server that it reports to. Your files stay on your computer unless you send them somewhere yourself.

## What goes over the network, and when

Lean Studio connects to other services only to do something you asked for or turned on:

- **Updates.** Once a day, unless you turn it off in Settings, it asks GitHub (`api.github.com`) for the latest Lean Studio release. A copy from the Microsoft Store does not do this; the Store updates it.
- **Lean and Mathlib.** Installing or switching a Lean toolchain runs elan, which downloads from GitHub. Getting a project's Mathlib runs Lake, which downloads Mathlib's prebuilt files. The app also asks GitHub which Lean releases exist.
- **Searching.** The search tools send what you type to Loogle (`loogle.lean-lang.org`) or LeanSearch (`leansearch.net`).
- **AI features.** Only if you add an API key. Your prompts, and the code and goals you choose to include, go to that provider (for example Anthropic or OpenAI) under that provider's own privacy policy. Lean Studio keeps the key in the macOS Keychain, the Linux keyring, or a private file in your Windows profile, and sends it only to that provider.
- **Remote projects.** If you open a project on another machine, Lean Studio connects to it with your own ssh setup.
- **Sharing.** "Open in Lean 4 Web" puts the file's text into a `live.lean-lang.org` link, which your browser opens.

Lean, Lake, elan, git and the other programs it runs connect to whatever they need for the job you started (for example a git remote).

## What stays on your computer

Settings, recent projects and logs are kept in your user profile. Lean Studio does not collect, sell or share personal information, and it is not meant for children under 13 to use without a parent.

## Contact

Questions about this policy: open an issue at https://github.com/keithadler/leanstudio/issues.

Last updated 26 November 2026.
