# Campaign registration and report, as a contract fixture

`contract-example/` is a **synthetic fixture, not a campaign result** - its own `Campaign` field says
so in both files. It exists so the pre-registration and report schemas have a worked instance that the
code is checked against, before any real campaign (#116, #122) is run.

## What the four files are

| file | what it pins |
| --- | --- |
| `spec.json` | the campaign as declared: 6 seeds, families with configs and endpoints, a `Budget` of 1,000 model tokens / 10 calls / 60 wall seconds, 2,000 bootstrap samples at seed 11, `FamilywiseAlpha` 0.05, test-partition hashes and the `Published` problem list |
| `frozen/registration.json` | `evolution-campaign-registration-v1`: the spec plus the three arms (`aidotnet`, `openevolve-recommended`, `openevolve-matched`), the six concrete seeds, and the rules, hashed to `RegistrationSha256` |
| `raw.json` | 30 rows, one per arm and seed, each with `Status` and `Value` |
| `report.json` | `evolution-campaign-report-v1`: 3 endpoints with `OneSidedP`, `HolmAdjustedP`, `MeanDifference` and `Interval95`, carrying `RawSha256` and the same `RegistrationSha256` |

## The three rules that make it a registration

Recorded verbatim in `frozen/registration.json`, before the data:

- **Stopping:** *"Fixed N = SeedCount per arm; no interim analysis, no extension, no re-run"*
- **Decision:** *"Holm step-down over every family x config one-sided bootstrap p; all must reject;
  ties fail"*
- **Published:** *"Strict majority of pre-declared problems matched or beaten (assumption A1)"*

The report links back by hash on both sides - raw data and registration - so a report cannot be paired
with a registration it was not derived under, which is the property the fixture is here to exercise.
`IntervalNote` states that `Interval95` is descriptive and unadjusted and that the decision is the Holm
step-down, so the interval cannot be read as the test.
