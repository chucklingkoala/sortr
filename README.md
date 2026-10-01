<img src="src/Sortr.App/Assets/icon-256.png" width="72" align="right" alt="" />

# Sortr

**Sortr moves files into the right folders by reading their names.**

**[⬇ Download Sortr.exe](https://github.com/chucklingkoala/sortr/releases/latest)**: a single file with nothing to install. It runs on 64-bit Windows 10/11.

You have a folder full of loose files whose names mention who or what they belong to:

```
Downloads\
  jane.doe.invoice.2024.pdf
  JaneDoe_contract.docx
  Doe_Jane-headshot.jpg
  acme-corp quarterly report.xlsx
  meeting notes - Acme Corp & Jane Doe.txt
  holiday.jpg
```

You also have a folder with one subfolder per person, client, project or other category. Sortr calls these **targets**:

```
Clients\
  Acme Corp\
  Jane Doe\
  Mary Sue\
```

Sortr reads every file name, works out which target it mentions, and shows you a list to check. When you're happy, one click moves each file into its target's folder:

```
Clients\
  Acme Corp\  acme-corp quarterly report.xlsx
  Jane Doe\   jane.doe.invoice.2024.pdf, JaneDoe_contract.docx, Doe_Jane-headshot.jpg
```

Files that mention no target stay where they are. For `holiday.jpg` that's because no target matched. `meeting notes - Acme Corp & Jane Doe.txt` mentions two targets, so it's left for you to decide.

It works with any file type: documents, photos, videos, music and so on. Only the file name matters.

## Using it

1. **Files to sort**: pick the folder of files to sort. Only files directly in that folder are scanned, not its subfolders.
2. **Target folders**: pick the folder that contains one subfolder per target. Each subfolder's name is the name Sortr searches for.
3. Click **Scan**. Every file is listed with the target it matched and a badge showing how confident the match is.
4. Review the list. Untick anything that's wrong, and pick a target for any file marked *Ambiguous*.
5. Click **Move selected**. If you change your mind, **Undo last move** puts that batch back where it came from.

The app follows Windows' light/dark setting and accent colour.

## How names are matched

File names are messy, so Sortr ignores capitalisation, accents and separators (`.`, `_`, `-`, spaces, brackets…). It also accepts the name in reverse order. With a target called **Jane Doe**:

| Badge | Meaning | Example file names | Ticked by default? |
|---|---|---|---|
| **Strong** | The name appears as separate words | `jane.doe.invoice.pdf`, `Doe_Jane.jpg`, `Jane Doe (2024).docx` | Yes |
| **Loose** | The name appears run together | `JaneDoe_contract.docx`, `janedoe.png` | Yes; worth a glance |
| **Ambiguous** | More than one target matched | `Jane Doe and Mary Sue.txt` | No; choose the target from the dropdown |
| **Unmatched** | No target matched | `holiday.jpg` | Can't be selected; hidden unless *Show unmatched* is ticked |

Some rules reduce false matches:
- **One-word targets** (e.g. "Nova") must appear as a separate word. `nova_logo.png` matches, but `casanova.pdf` doesn't.
- **Short names** (two words, fewer than 6 letters in total, e.g. "Ann Li") must appear as separate words. Otherwise "annli" would match inside unrelated text like `joannlivestream`.
- **Longer names win.** If you have both "Jane Doe" and "Jane Doe Smith", then `jane.doe.smith.pdf` goes to "Jane Doe Smith".

## Review shortcuts

- **Space** ticks or unticks all highlighted rows.
- **Double-click** a row to open the file.
- Right-click a row for **Show in Explorer**.
- Choosing a target from an ambiguous row's dropdown ticks that row.

## Safety

- **Nothing is overwritten.** If the target folder already has a file with the same name, the moved file is saved as `name (1).ext`.
- **Every move is logged** as it happens, to `%LocalAppData%\Sortr\undo\`. Undo works even if the app closed mid-move.
- **Undo is careful.** It only moves a file back if its original spot is still free. Anything it can't restore stays in the log, so you can retry.
- You confirm before anything moves.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet test
dotnet run --project src/Sortr.App
```

### Single executable

```
dotnet publish src/Sortr.App -p:PublishProfile=SingleFile
```

This produces `publish/Sortr.exe`, a single file of about 60 MB. It runs on any 64-bit Windows 10/11 PC without .NET installed. The settings live in [SingleFile.pubxml](src/Sortr.App/Properties/PublishProfiles/SingleFile.pubxml):
- The .NET runtime is bundled into the exe.
- The exe is compressed.
- WPF's native DLLs are bundled too. They're unpacked to a temp folder on first launch.

The exe isn't code-signed, so Windows SmartScreen may warn on first run. Click *More info → Run anyway*.

### Releasing

Push a version tag. GitHub Actions then runs the tests, builds `Sortr.exe` and publishes it as a GitHub Release, along with a SHA-256 checksum:

```
git tag v1.2.3
git push origin v1.2.3
```

The tag sets the version number stamped into the exe. The workflow is in [.github/workflows/release.yml](.github/workflows/release.yml).

### App icon

The icon is drawn in code by `tools/IconGen`. After editing it, regenerate the icon with this command. The optional second argument writes a preview sheet.

```
dotnet run --project tools/IconGen -- src/Sortr.App/Assets [preview.png]
```

## Project layout

- `src/Sortr.Core`: the matching, scanning, moving, undo and settings logic (no UI).
- `src/Sortr.App`: the Windows app (WPF, MVVM via CommunityToolkit.Mvvm, Fluent theme).
- `tests/Sortr.Tests`: xUnit tests.
- `tools/IconGen`: the icon generator (not part of the solution build).

## License

[MIT](LICENSE)
