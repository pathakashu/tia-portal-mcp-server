# Project Graph

Graph nodes include:

- PROJECT
- PLC
- BLOCK
- DB
- UDT
- TAG
- HMI
- SCREEN
- DEVICE
- MODULE
- LIBRARY

Edges include:

- CONTAINS
- CALLS
- READS
- WRITES
- USES
- REFERENCES
- DEPENDS_ON
- CONNECTED_TO
- BINDS_TO

Example:

```text
FB_Motor --USES--> DB_Motor
FB_Motor --READS--> Motor_Start
FB_Motor --WRITES--> Motor_Run
FB_Motor --CALLS--> FC_Safety
```
