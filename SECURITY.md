# Security policy

## Supported version

Only the newest approved owner/tester release is supported. Older ZIP files are fixed snapshots and do not receive security updates.

## Reporting a vulnerability

Do not place passwords, access tokens, refresh tokens, OBS addresses, unredacted logs, or personal viewer information in a public GitHub issue.

When the repository is connected, enable GitHub private vulnerability reporting and use it for security-sensitive reports. Until then, contact the owner privately and share only the minimum reproducible information. The Handler's safe diagnostic export is preferred over raw logs.

## Security model

- The ASP.NET Core server listens on `127.0.0.1` only.
- OBS, Twitch, and Spotify credentials are encrypted with ASP.NET Core Data Protection and Windows DPAPI.
- Credentials are tied to the current Windows account and computer.
- Tester packages contain no saved credentials, reports, viewer notes, histories, or logs.
- The product has no cloud database, remote telemetry, or owner-accessible tester account.

Any proposal to expose the server beyond loopback, synchronize data, or add remote control requires a new authentication, authorization, encryption, and threat-model review first.
