# Public subscription sources

The catalog keeps Epodonios and seeds two additional, removable sources once on
upgrade (catalog version 1). Existing entries with the same URL retain their name,
enabled state, ID and HTTP validator. Disabling or deleting an added source is
respected on subsequent starts. Only the original Epodonios source is mandatory.

| Source | Subscription | Repository |
| --- | --- | --- |
| Epodonios | `https://raw.githubusercontent.com/Epodonios/v2ray-configs/main/All_Configs_Sub.txt` | [Epodonios/v2ray-configs](https://github.com/Epodonios/v2ray-configs) |
| Radikal | `https://raw.githubusercontent.com/0xRadikal/Free-v2ray-Configs/main/verified/configs.txt` | [0xRadikal/Free-v2ray-Configs](https://github.com/0xRadikal/Free-v2ray-Configs) |
| NoMoreWalls | `https://raw.githubusercontent.com/peasoft/NoMoreWalls/master/list_raw.txt` | [peasoft/NoMoreWalls](https://github.com/peasoft/NoMoreWalls) |

On 2026-10-18 the additional URLs returned HTTP 200, with content commits on the
same date. Their downloaded bodies were 357,506 and 90,489 bytes. The actual app
extractor found 864 and 231 additional supported profile identities, respectively,
after deduplication against that day's Epodonios body and earlier sources. These
are candidate counts, not confirmed working servers. No live proxy probe was run
for source selection. Counts and availability change over time.

Barry-far's full list and V2RayAggregator's Eternity list were also inspected;
neither added a supported identity beyond the evaluated pool in that snapshot,
so they were not included by default. More URLs would add downloads without new
candidates in this sample.

Sources are downloaded with the existing bounded concurrency, byte limits and
per-source error handling. Profiles merge by canonical identity; identical
configurations do not become extra rows. Downloading a source never grants local
Working health: the phone's own validation still decides home eligibility. The
simple home screen and its five/ten healthy-server targets are unchanged.
