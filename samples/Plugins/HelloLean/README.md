# Hello Lean, an example plugin

A Lean Studio plugin is a .NET class library that references `LeanStudio.Plugins` and has a public class
implementing `ILeanStudioPlugin`. This one adds two commands to the command palette and writes a line to Output
each time a Lean file is saved:

- **Hello Lean: Sorries in This File** lists the file's sorries and goes to the first.
- **Hello Lean: #check the Selection** checks the selected term with the file's imports.

## Build and install

Build it straight into the plugins folder, then restart Lean Studio. *Plugins: Open the Plugins Folder* in the
command palette shows where that is (`~/Library/Application Support/LeanStudio/plugins` on a Mac,
`%APPDATA%\LeanStudio\plugins` on Windows, `~/.config/LeanStudio/plugins` on Linux).

```bash
dotnet build -c Release -o ~/Library/Application\ Support/LeanStudio/plugins/HelloLean
```

Output says `Plugin: Hello Lean (HelloLean.dll)` when it loads, or why it didn't.

## What a plugin can do

`IPluginHost`, which `Initialize` receives, gives a plugin:

- `AddCommand(title, run)`: a command in the palette, which keybindings.json can bind to a key.
- `ActiveDocument`: the file in the editor (path, text, caret, selection), with `Replace` and `Insert` as undoable edits.
- `MessagesOf(path)`: Lean's errors, warnings and information for an open file.
- `CheckLeanAsync(code)`: check some Lean with the project's Lean and dependencies.
- `RunAsync(program, args)`: run a program in the project folder (`lake` and `lean` come from elan).
- `OpenFileAsync(path, line, column)`, `Log(line)`, and the `FileOpened` and `FileSaved` events.

A plugin runs with Lean Studio's permissions, like an editor extension: install only plugins you trust.
