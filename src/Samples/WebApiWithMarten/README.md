# WebApiWithMarten

This is a very simple project just showing the usage of Marten with Wolverine in an ASP.Net Core project. To run the
project,
you'll first want to run the Docker compose file at the root of the Wolverine solution like so:

```bash
docker compose up -d
```

or if you want to use your own PostgreSQL database, just change the connection string in the `appsettings.json` file.

Once you have the database, just run the project from Rider/Visual Studio.Net/VS Code or even the command line, and it
should pop a browser up
to the Swashbuckle page at [http://localhost:5215/swagger/index.html](http://localhost:5215/swagger/index.html) where
you can execute the endpoints. Wolverine itself will happily build out all necessary database tables for you on startup.
