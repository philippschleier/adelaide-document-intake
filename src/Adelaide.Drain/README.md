# Adelaide.Drain

A small console tool that moves scanned or faxed PDFs from a staging location into Adelaide. In production it bridged
an on-premises scanner workflow to the cloud deployment. The web host later gained an in-app poller for the same share
(`DrainPoller`), which is the long-term path. This tool documents the at-least-once handover pattern both use.

**How it works**
1. List files in the source: an Azure Files share via SAS (`ADELAIDE_DRAIN_SHARE_URL`), or a local folder for testing
   (`ADELAIDE_DRAIN_SHARE`).
2. For each file not yet marked *sent* in its local SQLite ledger, acquire an app-only Entra token (client
   credentials, `Drain` app role) and `POST` the file to `/api/drain/upload`.
3. Mark the file *sent* **only after** a 2xx response carrying a document id (ledger-after-ack). The idempotency key is
   the share-relative path (`SourceRecordId`), so a re-delivery is skipped and nothing is sent twice or lost.
4. Optionally delete the share copy after a confirmed send. Transient failures retry with a cap; then the file is
   dead-lettered in the ledger.

It runs as a single instance (named mutex), either one pass per invocation (scheduled task) or `--loop`.

**Configuration:** see the header of [`Program.cs`](Program.cs). Every value comes from environment variables, and
none has a default pointing at real infrastructure.
