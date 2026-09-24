# Third-party notices: the Lean infoview

The files in `iv/` are the built distribution of **@leanprover/infoview 0.13.0**
(https://github.com/leanprover/vscode-lean4), unmodified, which Lean Studio serves to the browser so that Lean's own
infoview (with ProofWidgets and other user widgets) can connect to it. `index.html` is Lean Studio's own.

- **@leanprover/infoview** and **@leanprover/infoview-api**: Apache License 2.0, copyright the vscode-lean4
  contributors. The license text is in `LICENSE-Apache-2.0.txt`.
- Bundled with it, under their own licenses, whose notices are kept in the bundled files:
  - React, React DOM: MIT, copyright Meta Platforms, Inc. and affiliates.
  - es-module-shims: MIT, copyright Guy Bedford.
  - vscode-languageserver-protocol: MIT, copyright Microsoft Corporation.
  - @vscode-elements/react-elements and the Lit libraries it uses: MIT, and BSD-3-Clause, copyright Google LLC.
  - tachyons, react-fast-compare, es-module-lexer: MIT.
  - mhchem (as bundled): MIT, copyright Martin Hensel.
  - The codicons font (`codicon.ttf`): CC BY 4.0, copyright Microsoft Corporation.

Lean Studio itself is MIT licensed (see the repository's LICENSE).
