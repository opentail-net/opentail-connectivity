# Safety and credentials

Most of this library reads. A few parts can change things in a mailbox or calendar, or send mail. This page lists them, what
guards each one, and what it leaves to you.

## What can write

| Capability | Where | Guard |
|---|---|---|
| Fetch mail | `IInboxAdapter` (IMAP, Graph, Gmail) | Read-only contract. `ReadOnlyInboxGuard.AssertReadOnly(type)` fails if an adapter has public methods named like `Send`, `Delete`, `Move`, `Mark` and so on; it checks names only, and only when you call it. IMAP opens folders with `FolderAccess.ReadOnly`. |
| Send mail, delete, mark read, move | `IEmailSender` (IMAP/SMTP, Graph, Gmail) | `PolicyEnforcingEmailSender` and `EmailSenderPolicy` (below). Delete, mark-read and move **pass through the policy unchecked**. |
| Create, update, delete, respond to events | `ICalendarAdapter` on Graph and Gmail | `CalendarWritePolicy`, passed to the adapter's constructor, checked before any provider call. The default, `Disabled`, refuses every write (`CalendarWriteDeniedException`). Opt in per kind (`AllowCreate`, `AllowUpdate`, `AllowDelete`, `AllowRespond`) and add `Approve` for a person's sign-off. `Respond` tells the organiser (`SendsExternalResponse`). The ICS adapter refuses all writes (`NotSupportedException`). |
| Everything else (folders, web, CSV, `.eml`, SQL, social) | Source connectors | Read only. |

If you wire an agent or other automation to these types, expose only what it needs: hand it an `ICalendarReader` (the read half
of `ICalendarAdapter`), keep the write policy `Disabled`, or wrap the adapter in `ReadOnlyCalendarAdapter`, which refuses every
write. When an agent may write, allow only the kinds it needs and route `Delete` and `Respond` through `Approve`.

## Email send policy

`EmailSenderPolicy` defaults are restrictive (`EmailSenderPolicy.Disabled`):

| Setting | Default | Effect |
|---|---|---|
| `SendingEnabled` | `false` | Every send throws `EmailSendPolicyException`. |
| `AllowedRecipientDomains`, `AllowedRecipientAddresses` | empty | If either is set, every To and Cc address must match one of them. Both empty means **any recipient**. |
| `MaxEmailsPerHour`, `MaxEmailsPerDay` | `0` | Rate limits. `0` means **no limit**. |
| `ReplyOnlyMode` | `false` | Sends must set `InReplyToMessageId`. Recipients are not checked against the original thread. |
| `RequireHumanApproval` | `true` | Sends need an approval gate: `PolicyEnforcingEmailSender` must be given an `IPendingSendStore`, and a person calls `EmailSendApprovalService.ApproveAsync` (or `RejectAsync`). |

Limits to be aware of:

- A policy that sets only `SendingEnabled = true` and `RequireHumanApproval = false` therefore sends anything to anyone. Set the
  recipient lists and limits explicitly.
- Rate-limit counters live in memory, per `PolicyEnforcingEmailSender` instance. They reset on restart and are not shared between
  processes.
- With approval on, recipients are checked both when a send is queued and again when it is approved. Approval also reserves a
  rate-limit slot before sending. Build the service with `PolicyEnforcingEmailSender.CreateApprovalService()` so queued and
  approved sends use the same policy and limiter; a separately constructed service can split the rate cap.
- `InMemoryPendingSendStore` is in memory unless you pass it a `filePath`, in which case it also saves to and loads from that file.
  Use that, or your own `IPendingSendStore`, if pending sends must survive a restart.
- The policy only guards the `IEmailSender` you wrapped. Nothing stops code that holds a raw sender or the provider SDK.

## OAuth scopes (Gmail)

`GmailScopes` offers four sets:

| Set | Scopes | Use |
|---|---|---|
| `ReadOnly` | `gmail.readonly` | Read mail only. Prefer this for ingestion. |
| `SendWithoutRead` | `gmail.send` | Send only; cannot read or label mail. |
| `SendOnly` | `gmail.send`, `gmail.modify` | Send and modify, no calendar. Despite the name, `gmail.modify` also allows reading all mail. |
| `Full` | `gmail.readonly`, `gmail.send`, `gmail.modify`, `calendar` | Everything, including full calendar write access. |

The library does not obtain refresh tokens; you do, with your own consent flow, and the scopes granted then are what the token
can do. `GmailCredentialProvider` takes the set its flow asks for (`scopes`, default `Full`), but asking for less does not narrow a
token that was granted more. Use these sets when you obtain the token, request the narrowest one that does the job, and pass the
same set to the provider. A refresh token issued with `Full` can modify the mailbox
and calendar even if your code only reads.

For Microsoft Graph, the adapters use the client-credentials flow with the `.default` scope, so the app registration's granted
application permissions decide what the client can do. Grant read-only ones (for example `Mail.Read`, `Calendars.Read`) unless
you need more. Application permissions apply to every mailbox in the tenant, not just `userEmail`; an Exchange administrator can
limit the app to specific mailboxes (RBAC for Applications, or the older application access policies).

## Credentials

- `ImapSettings`, `GmailSettings` and `GraphSettings` are plain records that hold passwords, client secrets and refresh tokens.
  Their auto-generated `ToString()` prints those values, so do not log the objects.
- Load secrets from environment variables or a secret store, not from source or config checked into git.
- `CredentialRedactor` replaces given secret values with `[REDACTED]` in text such as error messages. Build it with
  `new CredentialRedactor(secret1, secret2)` or `CredentialRedactor.FromEnv("VAR1", "VAR2")`. It matches exact values only.
- The Twitter/X connector needs a bearer token; the other social connectors take their own API credentials.
- `PerSecret<T>` keeps one built client per secret value and rebuilds it when the secret rotates.

## Ingested content

- Documents contain whatever the source contained, including personal data. Exporters write plain files: protect and expire the
  output as you would the source.
- `TenantContext` labels documents, but nothing in the library enforces separation between tenants. Keep tenants in separate
  stores if isolation matters.
- Treat ingested text as untrusted input, especially email, web pages and social posts. If you feed it to a language model, it
  can contain instructions aimed at that model.

## SQL connector

`SqlConnectorConfig` reads only the listed fields. Two cautions:

- With `IsQuery = true`, `TableOrQuery` is run as SQL as written, and is also embedded in a subquery when fetching a row. Never
  build it from user input. Table and field names are quoted with embedded double quotes escaped; they still must come from
  trusted configuration, not user input.
- The connector forces SQLite connections to `Mode=ReadOnly`, enables `PRAGMA query_only`, and rejects connection strings that
  explicitly permit writes. Keep the database file and its directory protected as well.

## Web crawling

`SiteCrawlConnector` honours `robots.txt` (`RespectRobotsTxt`, default on), only follows links on the start host, and can be limited with
`AllowedPathPrefixes` (whole path segments), `MaxPages`, `MaxDepth` and `RequestDelay`. Only crawl sites you have the right to crawl.
The same-origin check is complete only with a client that does not follow redirects itself (`CreateHttpClient`, or
`SiteCrawlConnector.Create(handler, ...)`); start URLs are trusted configuration, with no private-address blocking.
