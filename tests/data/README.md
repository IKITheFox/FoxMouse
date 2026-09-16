# Motion trace fixtures

`.fmotion.jsonl` uses integer microseconds and milli-DIP:

```json
{"kind":"header","schema":"foxmouse.motion/1","algorithm":"shake-v1","time_unit":"us","distance_unit":"milli_dip","normalized":true}
{"kind":"sample","t_us":8000,"device":0,"dx_mdip":12000,"dy_mdip":0,"buttons":0,"flags":0}
{"kind":"end","t_us":1000000,"sample_count":1}
```

Times must be strictly increasing. Samples from separate devices are never
combined into one gesture. Expectations live in sibling `.expect.json` files.

`buttons` is a bit mask (`left=1`, `right=2`, `middle=4`, `x1=8`, `x2=16`).
`flags` is a bit mask (`edge-estimated=1`, `discontinuity=2`,
`device-changed=4`). A trace must finish with an `end` record whose
`sample_count` matches the samples read; this makes truncated fixtures fail
closed.

Expectation example:

```json
{
  "schema": "foxmouse.expect/1",
  "config": "default-v1",
  "trigger_count": 1,
  "first_trigger_us": { "min": 120000, "max": 140000 },
  "release_by_us": 600000,
  "max_score": { "min": 0.95, "max": 1.0 }
}
```

Generate and verify the built-in corpus with:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project .\src\FoxMouse.TraceTool\FoxMouse.TraceTool.csproj -- `
  generate .\tests\data\generated

& 'C:\Program Files\dotnet\dotnet.exe' run `
  --project .\src\FoxMouse.TraceTool\FoxMouse.TraceTool.csproj -- `
  verify .\tests\data
```
