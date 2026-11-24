# Asynchronous jobs API

Some operations are too large or too slow to run inside a single request/response — bulk point loads,
segment exports, batch file imports. For these, the API accepts the request, hands back a job id
immediately (`202 Accepted`), and processes the work in the background. This document is the companion to
[`REALTIME_API.md`](REALTIME_API.md); read that first for auth, the base URL, and the standard error
catalog.

**Note on HTTP verbs:** every endpoint below is `POST`, including the status and result checks. This
API's HMAC signing scheme always signs a request body, so — exactly like [`WEBHOOKS.md`](WEBHOOKS.md)'s
`/list`/`/test` endpoints — a plain `GET` with no body would have nothing to sign. Send an empty JSON
object (`{}`) as the body for status/result checks.

## 1 · Submitting a job

```
POST /api/v1/{channelCode}/jobs/{jobType}
```

The request body is specific to `jobType` (see each job type's own documentation — e.g.
[`BATCH_FILE_IMPORT.md`](BATCH_FILE_IMPORT.md) for file-import job types). A successful submission returns:

- `202 Accepted`
- `Location` response header: `/api/v1/{channelCode}/jobs/{jobId}/status`
- Body: `{ "Data": { "jobId": "<guid>" }, ... }`

An unknown `jobType` returns `NOT_FOUND`.

## 2 · Checking status

```
POST /api/v1/{channelCode}/jobs/{jobId}/status
```

Body: `{}`. Response `Data`:

| Field | Meaning |
|---|---|
| `status` | `REQUESTED` → `PROCESSING` → `READY` or `FAILED` |
| `jobType` | The job type this job was submitted as |
| `totalRows` / `processedRows` / `successRows` / `failedRows` | Row-level progress (jobs that aren't row-oriented leave these `null`/`0`) |
| `rowErrorSamples` | Up to 200 sample row-level error messages |
| `errorMessage` | Set only when `status == FAILED` — the whole-job failure reason |
| `result` | `{ "available": true, "expiresAt": "...", "url": "..." }` once `READY`; `null` otherwise |
| `createdAt` / `startedAt` / `finishedAt` | Timestamps |

A job id that doesn't exist, or belongs to a different channel, returns `NOT_FOUND` — the API never
reveals whether a job id exists under another channel.

## 3 · Downloading the result

```
POST /api/v1/{channelCode}/jobs/{jobId}/result
```

Body: `{}`. Behavior depends on the job's result type:

- JSON summary jobs (e.g. `point_load`, `point_correction_file_import`) return the summary object directly
  in `Data`.
- File-result jobs (e.g. `segment_export`, `transaction_file_import`) return the raw file
  (`Content-Type: text/csv` today) as the response body.

Calling this before the job is `READY` returns a validation error. Results are retained for a limited
time (`result.expiresAt` from the status response); after that, this endpoint returns `NOT_FOUND`.

## 4 · Completion events

Every job publishes exactly one `job.completed` (success) or `job.failed` (failure) event through the
event outbox when it finishes — never one event per row, regardless of how many rows the job processed.
Subscribe via [webhooks](WEBHOOKS.md) if you want to be notified instead of polling status.
