# Lean Studio in the Microsoft Store

The Store gets the same build as the Windows zips, packaged as an MSIX bundle (x64 and arm64). The Store signs the bundle, so it needs no certificate of ours.

## The files

- `packaging/windows/store/AppxManifest.template.xml`: the package's description of the app. It sets the `leanstudio` command (an app execution alias, the same name as winget's) and asks for `runFullTrust`.
- `packaging/windows/store/identity.json`: the values that tie the package to the Store listing, from Partner Center, Lean Studio, Product management, Product identity, and the Store ID that CI submits to.
- `packaging/windows/store/Assets/`: logos and tiles in every size Windows asks for. `make-assets.py` remakes them from `packaging/icons/leanstudio.png`.
- `packaging/windows/store/build-msix.ps1`: builds `artifacts/msix/LeanStudio_X.Y.Z.0.msixbundle` on Windows. `-TestSign` also signs it so it installs on a test PC.
- CI's **Microsoft Store package** job runs the script on every tag (and when the workflow is run by hand) and uploads the bundle as the `store-msixbundle` artifact. It is not attached to the GitHub release, because only the Store's signature lets it install.

## What a Store install changes in the app

`StorePackage.IsStoreInstall` (the app runs from `WindowsApps`) turns off the GitHub update check, since the Store updates the app, and makes the assistant setup point at the `leanstudio` alias. The install folder's path changes with every update, so a command that named it would break.

## Submitting a release

With the Store credentials set (below), there is nothing to do: after a tag's GitHub release, CI's **Submit to the Microsoft Store** job wraps the bundle in an `.msixupload` and runs `msstore publish`. That starts a new submission from the live one (listing, pricing and ratings carry over), replaces the package and submits it for certification, which takes from a few hours to three business days. Partner Center allows one submission in progress at a time, so the job fails if an earlier one is still in certification; run the job again once it has published.

Without the credentials the job skips itself, and the bundle goes up by hand:

1. Download `store-msixbundle` from the tag's run: `gh run download <run id> -n store-msixbundle -D store`.
2. Partner Center, Lean Studio, **Update**, **Packages**: upload `LeanStudio_X.Y.Z.0.msixbundle` (each upload needs a higher version than the last, which the release version gives), then **Submit for certification**.

### The Store credentials

CI signs in to Partner Center as an app registration in Microsoft Entra ID. Set it up once:

1. Partner Center, **Account settings**, **User management**, **Microsoft Entra applications**: **Create Microsoft Entra application** (or add an existing one), with the **Manager** role.
2. Open it and **Add new key**. Copy the client secret now: Partner Center shows it once. Keys expire; make a new one before then.
3. Note the **Tenant ID** and **Client ID** on the same page, and the **Seller ID** under **Account settings**, **Legal info**, **Developer**.
4. Add them to the repository's Actions secrets as `STORE_TENANT_ID`, `STORE_CLIENT_ID`, `STORE_SELLER_ID` and `STORE_CLIENT_SECRET`, for example `gh secret set STORE_CLIENT_SECRET -R keithadler/leanstudio`, which asks for the value.

`StoreId` in `identity.json` names the product the job submits to.

## The first submission

These are filled in once and carried over to later submissions.

- **Pricing and availability:** free, all markets.
- **Properties:** category Developer tools. Privacy policy URL: https://github.com/keithadler/leanstudio/blob/main/PRIVACY.md. Website: https://github.com/keithadler/leanstudio. Support: https://github.com/keithadler/leanstudio/issues.
- **Age ratings:** the questionnaire. No user-to-user communication, no purchases. AI features send prompts only to the provider the person adds a key for.
- **Restricted capability `runFullTrust`**, the reason Partner Center asks for:

  > Lean Studio is a desktop IDE for the Lean 4 theorem prover. It runs the Lean compiler, the Lake build tool, the elan toolchain manager, git and ssh as child processes on the user's own projects, and reads and writes project folders the user chooses anywhere on disk. That requires a full-trust desktop process.

- **Store listing** (English):
  - Description:

    > Lean Studio is an IDE for Lean 4, the programming language and theorem prover. Write definitions and proofs with the goal state beside your cursor, see errors as you type, and step through a proof one tactic at a time.
    >
    > New to Lean? The built-in tutorial is ten short lessons, from #eval 2 + 2 to induction and proving your own programs correct. Each ends in exercises that Lean checks, goals are read back in plain English, errors are explained, and a Playground lets you try things with no project to set up.
    >
    > It installs and switches Lean toolchains for you, sets up Mathlib projects with their prebuilt cache, and searches Mathlib with Loogle and LeanSearch. ProofWidgets, plots and the infoview work as in the browser. Optional AI features use your own Anthropic or OpenAI key, and the built-in MCP server lets coding assistants check proofs with the real Lean.
    >
    > Lean Studio is free and open source (MIT).

  - Short description: An IDE for the Lean 4 theorem prover.
  - Features: A ten-lesson tutorial for newcomers, with exercises Lean checks; Live goal state and errors; Lean toolchains and Mathlib set up for you; Mathlib search; ProofWidgets and the infoview; Optional AI with your own key; MCP server for coding assistants.
  - Keywords: Lean, Lean 4, theorem prover, proof assistant, Mathlib, formal verification, mathematics.
  - Screenshots: at least one at 1366x768 or larger, from a Windows PC (`tools/LeanStudio.Snapshot` makes the ones in `docs/images`).

## Not checked yet

The bundle has only been built in CI. It has not been installed on a Windows PC: try it with `-TestSign` before the first submission.
