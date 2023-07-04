# Best Practices

There will be more content here soon:)

* Minimize dependencies, put functionality directly into handler classes
* Utilize "compound handlers" to keep logic or transformations in pure functions for easier testability
* Lean on transactional middleware and/or cascading message returns when possible for easier testability
* Prefer method injection over constructor injection
* Try to only publish cascading events in the root message handler to make the message flow easier to understand
* "A-Frame" architecture
* One handler per message type unless trivial
