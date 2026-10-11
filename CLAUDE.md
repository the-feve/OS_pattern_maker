# OS_pattern_maker — working rules

Read `HANDOFF.md` §0 first; it holds the current state and the next step.

- **Code goes to Onshape by paste, not API.** After each change to `src/PatternMaker.fs`, the owner
  pastes the whole file into the Feature Studio (`Get-Content -Raw src\PatternMaker.fs | Set-Clipboard`).
- **FeatureScript gotchas and workflow rules live in a separate repo:**
  [OS_FeatureScript_Reference](https://github.com/the-feve/OS_FeatureScript_Reference). Read its
  `FeatureScript_Reference_Notes.md` before writing geometry code and `ONSHAPE_WORKFLOW.md` for how we
  work. **Log any new gotcha there, not in this repo.**
- **Onshape API calls are scarce.** Check math locally first; ask the owner for paste results and the
  Notices panel instead of calling the API.
- **The pure math has a Python twin.** `tools/pattern_math.py` mirrors the math functions at the bottom
  of `src/PatternMaker.fs` name for name. Change both together and run
  `python3 -m unittest tools.test_pattern_math` (about 100 s) and `python3 tools/render_previews.py`.
- **Units are mm**; angles in degrees at the dialog.
- Keep `HANDOFF.md` (status and decisions) and `docs/DESIGN.md` §6 (unverified API calls) current.
