# Snapshot receipt and ACK diagnostics

Receipt membership uses stable connection indices scoped to each delta snapshot system.
The first 64 indices use an inline word; higher indices use lazily allocated overflow words.
Disconnect clears local and retained in-flight receipts before recycling the index.
Reset starts a new index namespace and clears receipt credit.

Snapshot copying checks membership of every active recipient rather than comparing counts.
Sending still uses prediction expiry to resend values when acknowledgements do not arrive.
ACK updates compare current slot hashes against the latest accepted per-slot remote receipt.
Only slots actually sent in the acknowledged snapshot receive receipt updates. Completion
ANDs the recipient's receipt word across current entries, without extra remote hash lookups.

Cluster ACKs use a cluster-ID-to-list-index dictionary with swap removal. Pruning and pool
ownership still use the retained list. A wrapped ID that is already outstanding for a peer
is skipped rather than overwriting the old send; changed values stay pending for another ID.
Outgoing ACKs use the retained source only while its object/scene, ID and version are valid.
Incoming packets still resolve the object ID from the wire normally.

## Commands

- `net_snapshot_ack_start`: reset counters and begin counting in this process.
- `net_snapshot_ack_report`: print and overwrite `snapshot-acks.txt` while counting continues.
- `net_snapshot_ack_stop`: stop counting, print and save the final report.

The file is in the current process's data filesystem. For this editor project it is
`game/data/local/networkobjectload#local/snapshot-acks.txt`. Host and client reports are separate.
Counters cover the whole interval, including setup/warmup if started before the runner.

The report includes cluster ACK calls and ACK lookup probes, snapshot callbacks (cluster and
single-snapshot ACKs), remote slot update calls, local receipt slots visited, receipt add/remove
attempts, completion-word checks and expired-prediction resend attempts. A probe is a candidate
comparison in the old linear ACK lookup or one dictionary lookup in the keyed implementation;
it is not a CPU duration. Counts are diagnostic main-thread snapshot-system work, not packet
throughput. Counting is disabled by default. Keep enabled diagnostic runs separate from timing
comparisons, and leave `net_sync_track_start` off during timing runs.

## Focused validation

Run SboxBuild with `test --no-build` and filter `ConnectionMaskTest`, `RemoteSnapshotStateTest`,
`Receipt`, `PollingEntryDoesNotPrepareUnchangedDeltaProperties`,
`UnsaltedMutationInvalidatesReceipts`, `IdleSnapshotWakesOnMutation`, and
`IdleSnapshotsWakeForConnectionChanges` after a Developer build.

Requested timing plan: five actual-client `Changing/5000` repeats and one synthetic
`Changing/1000` case with 32 fake peers. Synthetic peers simulate ACK load; they do not establish
real 32-client transport throughput. Results are retained under
`D:/Sandbox.Public.Projects/results/network-object-load/receipt-masks-20261011`.
