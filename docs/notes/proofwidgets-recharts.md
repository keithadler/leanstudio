# ProofWidgets Recharts plots in the infoview

Notes from 1.9.5, where every ProofWidgets Recharts chart failed in Lean's infoview with **"Error: Invariant failed"**
(in the browser infoview and in the Infoview tab alike), plain ProofWidgets HTML rendered, and the Infoview tab was
sometimes left with nothing in it.

## Repro

A Lake project that depends on ProofWidgets (any recent version; seen with the recharts 2.15.4 bundle), built, with
this file, which is ProofWidgets' own `Demos/Plot.lean`:

```lean
import ProofWidgets.Component.HtmlDisplay
import ProofWidgets.Component.Recharts

open Lean ProofWidgets Recharts

def fn (x : Float) : Float := 50 * (x - 0.25) * (x - 0.5) * (x - 0.7)

def Plot (fn : Float → Float) (steps := 100) : ProofWidgets.Html :=
  let jsonData : Array Json :=
    Array.range (steps + 1)
    |> Array.map (fun (x : Nat) => let x : Float := x.toFloat / steps.toFloat;  (x, fn x))
    |> Array.map (fun (x,y) => json% {x: $(toJson x) , y: $(toJson y)});
  jsx%{<LineChart width={400} height={400} data={jsonData}>
    <XAxis domain?={#[toJson 0, toJson 1]} dataKey?="x" />
    <YAxis domain?={#[toJson (-1), toJson 1]} allowDataOverflow={Bool.false} />
    <Line type={.monotone} dataKey="y" stroke="#8884d8" dot?={Bool.false} />
  </LineChart>}

#html Plot fn
```

Put the cursor on `#html Plot fn` (line 20) and open the Infoview tab, or View ▸ Lean Infoview in Browser.

Check it in a real window (needs a desktop session; prints the page's address, so the same page can be opened in a
browser while `LEANSTUDIO_NATIVE_HOLD=<seconds>` keeps the window up). The line is 0-based:

    LeanStudio.Snapshot --native-recharts <project> <project>/Plot.lean 19

## Cause 1: two copies of the recharts module ("Invariant failed")

ProofWidgets puts `LineChart`, `XAxis`, `YAxis` and `Line` in one widget module (`widget/js/recharts.js`, which
bundles recharts). Its `HtmlDisplay` renders a chart's children at the same time, and each asks the infoview for its
module with `importWidgetModule(hash)`. In `@leanprover/infoview` 0.13 (which Lean Studio ships, unmodified) that
function puts a module in its cache only once its import has finished, and imports it from a new `blob:` URL
(`File` named `widget_<hash>.js`) each time. So the four components came from up to four separate copies of
recharts. Recharts 2.x passes a chart's axes to `Line` through React contexts made when the module loads, so a
`Line` from one copy inside a `LineChart` from another finds no axis and throws its invariant, which production
recharts reports as just "Invariant failed".

Shown outside Lean: one copy of `recharts.js` draws the chart, a `LineChart` from one copy with axes and line from
another throws "Invariant failed" from recharts' `useXAxisOrThrow`.

Fix (`src/LeanStudio.App/Infoview/index.html`): the page gives a `widget_<hash>.js` file the same `blob:` URL every
time. The hash names the module's source, so this is safe, and a browser loads one URL once, so every component
gets the same module. The vendored infoview is left unmodified. (Upstream, caching the import's promise rather than
its result would fix it for every host.)

## Cause 2: the infoview missed "the server started" (an empty Infoview tab)

The infoview learns that Lean is running, and where the cursor is, from events it subscribes to in React effects.
Those run after its first render, which happens a little after `loadRenderInfoview` returns. The page used to send
`ready` straight away, and the bridge answers with `serverRestarted` and `initialize`; when the answer came before
the effects had run, the events were lost and the infoview stayed on "Waiting for Lean server to start..." for good.
In the Infoview tab the bar is hidden, so that one small line is all there is: the tab looks empty. It happened in
the Infoview tab (WebKit) in one of four runs here, and in none of four after the fix.

Fix: the page waits for the infoview's first render, then a moment, before it delivers anything or sends `ready`.
The bridge also no longer answers `ready` with an empty `serverRestarted` (no capabilities, no version) when Lean
isn't running yet; the page waits for the real one, sent when Lean starts. (An empty one did not blank the page when
tried, so this is tidying, not a second cause.)
