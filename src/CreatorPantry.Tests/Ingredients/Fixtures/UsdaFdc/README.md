# USDA FoodData Central contract fixtures

Responses from `https://api.nal.usda.gov/fdc/v1/`, replayed by `UsdaFdcEvidenceGatewayTests` so the
adapter is tested against what FDC actually sends rather than what its published spec says. FDC data is
public domain under CC0 1.0, so committing it is fine.

Recorded **2026-10-20** with the `DEMO_KEY`, sent as the `X-Api-Key` header. Bodies are byte-for-byte as
received; no key appears in any of them.

| File | Request | Status |
|---|---|---|
| `foundation-flour-789890.fdc.json` | `GET food/789890` | 200 |
| `foundation-yogurt-2259793.fdc.json` | `GET food/2259793` | 200 |
| `sr-legacy-flour-169761.fdc.json` | `GET food/169761` | 200 |
| `branded-flour-2671058.fdc.json` | `GET food/2671058` | 200 |
| `search-flour.fdc.json` | `GET foods/search?query=flour wheat all-purpose&dataType=Foundation&dataType=SR Legacy&pageSize=5` | 200 |
| `error-api-key-invalid.fdc.json` | `GET food/789890` with a key that is not one | 403 |
| `error-over-rate-limit.fdc.json` | **Not recorded** — see below | 429 |

`GET food/1` answered 404 with an empty body, which the tests reproduce inline rather than as a file.

## What the recordings showed that the spec does not

- A food's `publicationDate` is `M/d/yyyy` (`4/1/2020`); a search hit's `publishedDate` is ISO.
- Nutrient unit names vary in case and spelling: `mg` here, `MG` in search, `µg` for micrograms.
- SR Legacy foods carry `foodPortions` (the spec says they do not). The volume is a word in `modifier`
  (`"cup"`) under a measure unit of `undetermined` (id 9999).
- Foundation foods mostly carry a single `RACC` portion, a serving reference rather than a measure.
- The yogurt reports energy only as Atwater factors (957, 958) and sugars as `269.3`.
- The `DEMO_KEY` reported `X-Ratelimit-Limit: 10`, not the 30 an hour the API guide states.

## The 429 is not recorded

Recording a throttle means spending the address's whole hourly quota and then having the key blocked for an
hour. `error-over-rate-limit.fdc.json` is **authored**: api.data.gov's `OVER_RATE_LIMIT` code in the same
`{ "error": { "code", "message" } }` shape as the recorded 403. The adapter classifies a throttle by status
alone, so nothing depends on that text.

## Re-recording

Re-record with a real key when FDC publishes a release that changes a shape the reader depends on. Fetch
the same ids, keep each body unmodified, and update the date above.
