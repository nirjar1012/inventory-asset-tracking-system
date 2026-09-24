# YardTracker documentation

This folder explains the whole project: what it is, why each piece exists, how the pieces
fit together, and what you have to know to run, change or present it.

Read in this order the first time:

| # | Document | What it answers |
|---|---|---|
| 1 | [01-project-overview.md](01-project-overview.md) | What problem does this solve, for whom, and why was it built this way? |
| 2 | [02-repository-map.md](02-repository-map.md) | What is every folder and file, and what is it for? |
| 3 | [03-database.md](03-database.md) | Every schema, table, procedure and view, and the reason for each |
| 4 | [04-service-and-contracts.md](04-service-and-contracts.md) | The WCF contracts, the service host, the client plumbing |
| 5 | [05-station-app.md](05-station-app.md) | The WPF scanning station on the yard floor |
| 6 | [06-modbus-and-gateway.md](06-modbus-and-gateway.md) | Modbus TCP from the spec, the PLC simulator, the edge gateway |
| 7 | [07-simulator.md](07-simulator.md) | How realistic demo data is produced |
| 8 | [08-etl-pipeline.md](08-etl-pipeline.md) | The incremental load into the reporting tables |
| 9 | [09-reporting.md](09-reporting.md) | SSRS operational reports and the Power BI executive model |
| 10 | [10-sharepoint-and-sops.md](10-sharepoint-and-sops.md) | The daily exception report and the operator SOP |
| 11 | [11-build-run-test.md](11-build-run-test.md) | Building, deploying, running, testing, troubleshooting |
| 12 | [12-end-to-end-walkthroughs.md](12-end-to-end-walkthroughs.md) | Traced journeys: one scan, one delivery, one machine fault, one ETL run |
| 13 | [13-glossary.md](13-glossary.md) | Yard, steel and platform vocabulary |

Already in this folder, and still current:

- [architecture.md](architecture.md) — condensed reference: processes, ports, ER diagram, register map
- [talking-points.md](talking-points.md) — the design decisions phrased for an interview or a demo

The top-level [../README.md](../README.md) is the quick start.
