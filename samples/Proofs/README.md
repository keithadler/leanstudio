# Proofs

A small Lake project (Lean `v4.34.0`, no Mathlib) that Lean Studio's tests, its headless snapshot run and the
README's screenshots all use. Each declaration in `Proofs/Basic.lean` is there to show a different outcome:

| Declaration | What it shows |
|---|---|
| `double`, `double_eq_two_mul` | A definition and a finished proof: Tenet verifies it (✓). |
| `and_swap` | A proof whose steps add and remove hypotheses, for the tactic state's diff. |
| `em'`, `not_not_elim` | An axiom the project adds, and a theorem that rests on it (◐, "Why?" names `em'`). |
| `unfinished` | A theorem left as `sorry`, for Prove It and the Sorries panel. |

Open it in Lean Studio (`dotnet run --project src/LeanStudio.App -- samples/Proofs`), or build it with
`lake build` from this folder.
