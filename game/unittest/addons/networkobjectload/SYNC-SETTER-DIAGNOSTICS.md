# Sync setter diagnostics

Run these protected commands in the host or client developer console independently:

1. `net_sync_track_start` clears the previous capture and enables counting.
2. Exercise the workload.
3. `net_sync_track_report` prints and saves the current report while continuing capture.
4. `net_sync_track_stop` stops counting, prints and saves the final report.

Each report overwrites `sync-setters.txt` in that process's `FileSystem.Data` filesystem.
The report ranks component type/property pairs across all instances by local unequal-value
assignments, with calls as the tie breaker. It includes counts and local changes per second.

- Calls: all sync wrapper invocations, including invalid components.
- Local changes: valid-component local assignments that pass the existing equality check.
  These can include unnetworked properties and assignments rejected by authority checks.
- Unchanged: assignments rejected by the existing local equality check.
- Replication updates: assignments through the controlled registered-entry path that advance
  the network table's snapshot revision. This is not a packet or snapshot count.
- Incoming: wrapper invocations while applying received network changes. These bypass the
  local equality check and are separate from local changes.

No extra getter calls, value serialization, component references or property values are recorded.
Tracking is off by default. At most 4,096 property pairs are retained; overflow calls are reported.
Counting adds instrumentation overhead, so use a separate diagnostic run from timing comparisons.
Reports requested during capture are approximate live counters; stop the workload before taking
the final report if setters can run on background threads.
Collection mutation without a sync setter, query-property changes and transform replication are
outside this tracker. Counts identify frequently assigned properties, not their CPU cost.
