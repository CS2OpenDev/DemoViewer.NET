# Rule Canvas testbed

Small rulesets for checking the Rule Canvas by hand, one structure family per file, so a check never
starts with a search through the shipped corpus. Run `scripts/rule-canvas-testbed.sh` (add `--reset` to
discard edits, `--no-build` to skip the build): it copies these files to
`~/.cache/demoviewer-rule-canvas-testbed/rules` and starts the Release desktop app with that folder as the
user rules directory, where the picker labels them `testbed`. To use another build, run it with `--prepare`
instead: it copies the files and prints the `CS2DEMOKIT_USER_RULES_DIR=...` line to launch that build with.
Edit the copies in the app, never these files.
While the app runs this way its analysis reads the testbed instead of your own user rules, and the
library may re-scan demos once because the fixtures add highlights. `FixtureRulesetsTests` checks that
each whole fixture composes with nothing said and that the two warning fixtures say exactly what they
should.

| Fixture | Test with it |
| --- | --- |
| `tb_counts` | Facet chips (`enemy`, `headshot`), a `match: { actor: any }` count, a `sum:` on `damage_dealt`, a `capture:` with `keep: first`. |
| `tb_gates` | A `MatchLive` context node wired into `live_deaths`, a `round.active` gate, an expression gate, `when:` rows joined by `or` and by `and`. |
| `tb_defines` | A trigger define with facets fired on by two stats, and a define with its own `while:`. |
| `tb_defines_warn` | One warning: nothing fires on `unused_kill`. Wire it in and the warning goes. |
| `tb_flags` | A four-row `or` flag, a two-row `and` flag, an expression flag, an `on:`/`off:` latch with two trigger rows. |
| `tb_highlights` | Two highlights with `when`, `title`, `score` and `group`: one over a count threshold, one over a flag. |
| `tb_show` | A table of four labelled columns (one `as: time`) and a scoreboard of three, one a compute with `format: F2`. |
| `tb_params_use` | A bounded `params:` entry read by a highlight, and `use: [kast]` with a compute reading `kast.kast_pct`. |
| `tb_foreach` | `kills_{weapon}` expanded by `for_each: { weapon: [ak47, awp] }`. |
| `tb_events` | Event nodes for `raw.round_start`, `round_ended` (one node, several wire events), `bomb_planted`, `damage_dealt` (player_hurt). |
| `tb_kinds` | One each of `compute`, `tally`, `streak`, `bucket`, `rate` and `burst`, all valid. |
| `tb_incomplete` | Unfinished on purpose: a sum with no `on:`, a blank highlight `when:`, an entry the loader drops, an unknown key. `enabled: false`, so only the canvas reads it. |
