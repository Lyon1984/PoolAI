# External adapters

Release 1 contains `PoolAI.Adapters.OpenAI`, which implements Gateway abstractions and normalizes OpenAI/Codex request, response, SSE, usage, and error behavior.

Adapters do not own business tables, quota transactions, subscriptions, Redis coordination, or endpoints. Vendor SDK types stop at the adapter boundary.
