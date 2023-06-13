# OptimizedArtifactWorkflowSample

This is a very simple project shows the impact of using Wolverine's `OptimizeArtifactWorkflow()` functionality. To run the project,
you'll first want to run the Docker compose file at the root of the Wolverine solution like so:

```bash
docker compose up -d
```

or if you want to use your own PostgreSQL database, just change the connection string directly in the `Program` file.

Once you have the database, just run the project from Rider/Visual Studio.Net/VS Code or even the command line, and as it's
in `Development` mode by default, you'll see it create PostgreSQL tables automatically for the node and envelope storage.

If you edit the `/Properties/launchSettings.json` file to this:

```json
{
  "profiles": {
    "OrderSagaSample": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Production"
      }
    }
  }
}
```

and restart the application, Wolverine will **not** try to migrate the system database tables. Wolverine will also assume
that all generated code is pre-built into the main application assembly. 
