# Community Health Index

SaePar Tunnel can read a public, read-only JSON index that ranks known config
profiles by aggregated connection quality. The app never needs a GitHub token
for this feature and does not upload user data directly to GitHub.

Recommended repository layout:

```text
SaePar-health/
  ranked-profiles.json
  README.md
```

Use the raw HTTPS URL in the app settings:

```text
https://raw.githubusercontent.com/<owner>/<repo>/main/ranked-profiles.json
```

## JSON Format

```json
{
  "schemaVersion": 1,
  "generatedAtUtc": "2026-08-10T12:00:00Z",
  "profiles": [
    {
      "profileId": "sha256-profile-id-from-app",
      "endpoint": "vpn.example.com:443",
      "protocol": "VLESS",
      "medianLatencyMs": 180,
      "successCount": 42,
      "failureCount": 5,
      "successRate": 0.893,
      "score": 91,
      "lastSeenUtc": "2026-08-10T11:58:00Z",
      "network": "ws",
      "region": "IR"
    }
  ]
}
```

Matching priority:

1. `profileId`
2. `protocol + endpoint`

`score` is optional. If it is missing, the app derives a conservative score from
success rate, sample count, and median latency.

## Privacy Rules

Do not publish raw IP addresses of users, device IDs, app IDs, personal notes,
exact local ISP identifiers, or full connection logs. A future upload pipeline
should be opt-in, anonymized, rate-limited, and should aggregate reports on a
server before updating this repository.

## App Behavior

Community data helps sort configs and can provide a fallback recommendation
when no local Full-Test profile exists. It never marks the VPN as connected by
itself; the app still runs the final internet validation before showing a
successful connection state.
