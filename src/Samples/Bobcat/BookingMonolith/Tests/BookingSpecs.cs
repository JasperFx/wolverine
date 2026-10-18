using Booking;
using Identity;
using FlightRecord = global::Flight.Flight;
using PassengerRecord = global::Passenger.Passenger;

namespace BookingMonolith.Tests;

/// <summary>
/// The booking-modular-monolith conversion: four modules (Identity, Passenger, Flight, Booking) in one
/// process. Identity and Passenger talk only over a durable local queue -- registering a user makes the
/// Passenger module create a passenger stub, and that is the one place the modules touch. Booking is
/// event-sourced in Marten with an inline snapshot, so the booking specs assert the event the request
/// appended and the aggregate rebuilt from its stream, not a row the POST wrote.
/// </summary>
/// <remarks>
/// Reuses the host's own command and document types via the project reference. RegisterUser requires a
/// first and last name, and CreatePassenger a passport number, which no spec is about; the host has no
/// airport or aircraft endpoints, so the flight number is the only human-readable identity a flight has.
/// </remarks>
[BobcatFeature("Booking Monolith")]
public class BookingSpecs(BookingMonolithHost app) : BookingMonolithSpec(app)
{
    private const string FirstName = "Spec";
    private const string LastName = "User";
    private const string PassportNumber = "SPEC-0001";

    private Guid _userId;
    private Guid _passengerId;
    private Guid _flightId;
    private Guid _bookingId;
    private int _lastStatusCode;
    private List<FlightRecord> _flights = [];
    private List<BookingRecord> _bookings = [];

    // ---- identity ---------------------------------------------------------------------------

    [BobcatSpec]
    public async Task register_a_user()
    {
        await WhenAUserRegisters("booking.user@example.com", "Password1!");

        ThenTheResponseStatusIs(201);
        await ThenTheStoredUserHasEmail("booking.user@example.com");
    }

    [BobcatSpec]
    public async Task registering_a_user_creates_a_passenger_stub()
    {
        await WhenAUserRegisters("stub.user@example.com", "Password1!");

        await ThenAPassengerStubExistsForThatUser();
    }

    [BobcatSpec]
    public async Task register_with_bad_email_returns_400()
    {
        await WhenAUserRegisters("not-an-email", "Password1!");

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task register_with_short_password_returns_400()
    {
        await WhenAUserRegisters("shortpwd@example.com", "abc");

        ThenTheResponseStatusIs(400);
    }

    // ---- passengers -------------------------------------------------------------------------

    [BobcatSpec]
    public async Task create_a_passenger()
    {
        await WhenAPassengerIsCreated("John Traveler", 30);

        ThenTheResponseStatusIs(201);
        await ThenTheStoredPassengerIs("John Traveler", 30);
    }

    [BobcatSpec]
    public async Task create_passenger_with_empty_name_returns_400()
    {
        await WhenAPassengerIsCreated("", 25);

        ThenTheResponseStatusIs(400);
    }

    // ---- flights ----------------------------------------------------------------------------

    [BobcatSpec]
    public async Task create_a_flight()
    {
        await WhenAFlightIsCreated("NYC-LAX", 299.99m);

        ThenTheResponseStatusIs(201);
        await ThenTheStoredFlightIs("NYC-LAX", 299.99m);
    }

    [BobcatSpec]
    public async Task create_flight_with_zero_price_returns_400()
    {
        await WhenAFlightIsCreated("BOS-SFO", 0m);

        ThenTheResponseStatusIs(400);
    }

    [BobcatSpec]
    public async Task get_flights()
    {
        await GivenAFlight("ORD-MIA", 199.99m);

        await WhenAllFlightsAreRead();

        ThenTheFlightsInclude("ORD-MIA");
    }

    [BobcatSpec]
    public async Task get_flight_by_id()
    {
        await GivenAFlight("SEA-DEN", 149.99m);

        await WhenTheFlightIsRead(_flightId);

        ThenTheResponseStatusIs(200);
    }

    [BobcatSpec]
    public async Task get_flight_by_id_returns_404_for_missing()
    {
        await WhenTheFlightIsRead(Guid.Empty);

        ThenTheResponseStatusIs(404);
    }

    // ---- bookings: event-sourced ------------------------------------------------------------

    [BobcatSpec]
    public async Task create_a_booking()
    {
        await GivenAPassenger("Booker Passenger", 28);
        await GivenAFlight("ATL-PHX", 179.99m);

        await WhenTheFlightIsBookedForThePassenger();

        ThenTheResponseStatusIs(201);
        ThenEvents(Specify<BookingCreatedDomainEvent>()
            .With(x => x.PassengerName, "Booker Passenger")
            .With(x => x.FlightNumber, "ATL-PHX")
            .With(x => x.Price, 179.99m));
        await ThenAggregate<BookingRecord>(TheStartedStream<BookingRecord>(), Specify<BookingRecord>()
            .With(x => x.PassengerName, "Booker Passenger")
            .With(x => x.FlightNumber, "ATL-PHX")
            .With(x => x.Price, 179.99m));
        await ThenTheStoredBookingIs("Booker Passenger", "ATL-PHX", 179.99m);
    }

    [BobcatSpec]
    public async Task booking_for_a_passenger_that_does_not_exist_returns_400()
    {
        await GivenAFlight("DTW-LAS", 159.99m);

        await WhenTheFlightIsBookedForAnUnknownPassenger();

        ThenTheResponseStatusIs(400);
        ThenNoEvents();
    }

    [BobcatSpec]
    public async Task booking_a_flight_that_does_not_exist_returns_400()
    {
        await GivenAPassenger("No Flight Passenger", 35);

        await WhenAnUnknownFlightIsBookedForThePassenger();

        ThenTheResponseStatusIs(400);
        ThenNoEvents();
    }

    [BobcatSpec]
    public async Task get_bookings()
    {
        await GivenAPassenger("Get Booker Passenger", 22);
        await GivenAFlight("CLT-SLC", 189.99m);
        await GivenTheFlightWasBookedForThePassenger();

        await WhenAllBookingsAreRead();

        ThenTheBookingsIncludeTheNewBooking();
    }

    [BobcatSpec]
    public async Task get_booking_by_id()
    {
        await GivenAPassenger("ById Passenger", 40);
        await GivenAFlight("MSP-TPA", 210.00m);
        await GivenTheFlightWasBookedForThePassenger();

        await WhenTheBookingIsRead();

        ThenTheResponseStatusIs(200);
    }

    // ---- identity steps ---------------------------------------------------------------------

    // A When step goes through WhenActing, so what the request appended (cascades included) is what
    // ThenEvents and ThenNoEvents judge. A Given step is a plain call: it arranges, it is not the act.
    public Task WhenAUserRegisters(string email, string password)
        => WhenActing($"a user registers with email \"{email}\" and password \"{password}\"", _ => register(email, password));

    private async Task register(string email, string password)
    {
        var result = await TrackedAsync(() => PostJsonAsync<RegisterUser, UserAccount>(
            "/api/identity/register", new RegisterUser(email, FirstName, LastName, password)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _userId = result.Body.Id;
    }

    // ---- passenger steps --------------------------------------------------------------------

    [Given("a passenger named \"{name}\" aged {age}")]
    public Task GivenAPassenger(string name, int age) => createPassenger(name, age);

    public Task WhenAPassengerIsCreated(string name, int age)
        => WhenActing($"a passenger named \"{name}\" aged {age} is created", _ => createPassenger(name, age));

    private async Task createPassenger(string name, int age)
    {
        var result = await TrackedAsync(() => PostJsonAsync<global::Passenger.CreatePassenger, PassengerRecord>(
            "/api/passengers",
            new global::Passenger.CreatePassenger(name, PassportNumber, global::Passenger.PassengerType.Unknown, age)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _passengerId = result.Body.Id;
    }

    // ---- flight steps -----------------------------------------------------------------------

    [Given("flight {flightNumber} priced {price}")]
    public Task GivenAFlight(string flightNumber, decimal price) => createFlight(flightNumber, price);

    public Task WhenAFlightIsCreated(string flightNumber, decimal price)
        => WhenActing($"flight {flightNumber} priced {price} is created", _ => createFlight(flightNumber, price));

    private async Task createFlight(string flightNumber, decimal price)
    {
        var departs = DateTime.UtcNow.AddDays(30);
        var command = new global::Flight.CreateFlight(
            FlightNumber: flightNumber,
            AircraftId: Guid.NewGuid(),
            DepartureAirportId: Guid.NewGuid(),
            ArriveAirportId: Guid.NewGuid(),
            DurationMinutes: 300,
            Price: price,
            DepartureDate: departs,
            ArriveDate: departs.AddHours(5),
            FlightDate: departs);

        var result = await TrackedAsync(() => PostJsonAsync<global::Flight.CreateFlight, FlightRecord>("/api/flights", command));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _flightId = result.Body.Id;
    }

    public Task WhenAllFlightsAreRead() => WhenActing("all flights are read", async _ =>
    {
        var result = await GetJsonAsync<List<FlightRecord>>("/api/flights");
        _lastStatusCode = result.StatusCode;
        _flights = result.Body ?? [];
    });

    public Task WhenTheFlightIsRead(Guid id) => WhenActing($"the flight with id {id} is read", async _ =>
    {
        var result = await GetJsonAsync<FlightRecord>($"/api/flights/{id}");
        _lastStatusCode = result.StatusCode;
    });

    // ---- booking steps ----------------------------------------------------------------------

    [Given("the flight was booked for the passenger")]
    public Task GivenTheFlightWasBookedForThePassenger() => book(_passengerId, _flightId);

    public Task WhenTheFlightIsBookedForThePassenger()
        => WhenActing("the flight is booked for the passenger", _ => book(_passengerId, _flightId));

    // Fresh ids rather than Guid.Empty: an empty id is rejected by the command's validator before the
    // endpoint runs, whereas an id nobody stored exercises the [Entity] OnMissing path the endpoint
    // advertises -- a missing referenced entity is bad input, so 400 rather than 404.
    public Task WhenTheFlightIsBookedForAnUnknownPassenger()
        => WhenActing("the flight is booked for a passenger that does not exist", _ => book(Guid.NewGuid(), _flightId));

    public Task WhenAnUnknownFlightIsBookedForThePassenger()
        => WhenActing("a flight that does not exist is booked for the passenger", _ => book(_passengerId, Guid.NewGuid()));

    private async Task book(Guid passengerId, Guid flightId)
    {
        var result = await TrackedAsync(() => PostJsonAsync<CreateBooking, BookingRecord>(
            "/api/bookings", new CreateBooking(passengerId, flightId, Description: null)));

        _lastStatusCode = result.StatusCode;
        if (result.Body is not null) _bookingId = result.Body.Id;
    }

    public Task WhenAllBookingsAreRead() => WhenActing("all bookings are read", async _ =>
    {
        var result = await GetJsonAsync<List<BookingRecord>>("/api/bookings");
        _lastStatusCode = result.StatusCode;
        _bookings = result.Body ?? [];
    });

    public Task WhenTheBookingIsRead() => WhenActing("the booking is read by id", async _ =>
    {
        var result = await GetJsonAsync<BookingRecord>($"/api/bookings/{_bookingId}");
        _lastStatusCode = result.StatusCode;
    });

    // ---- assertions -------------------------------------------------------------------------

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    [Then("the stored user has email \"{email}\"")]
    public async Task ThenTheStoredUserHasEmail(string email)
        => SpecAssert.Check("email", (await GetJsonAsync<UserAccount>($"/api/identity/{_userId}")).Body?.Email, email);

    /// <summary>
    /// The stub is written by the Passenger module's UserCreated handler, off a durable local queue, under
    /// the user's own id -- so reading it back is the only way to observe that the cascade between the two
    /// modules actually arrived.
    /// </summary>
    [Then("a passenger stub exists for that user")]
    public async Task ThenAPassengerStubExistsForThatUser()
    {
        var result = await GetJsonAsync<PassengerRecord>($"/api/passengers/{_userId}");
        SpecAssert.Check("status", result.StatusCode, 200);
        SpecAssert.Check("id", result.Body?.Id, _userId);
    }

    [Then("the stored passenger is named \"{name}\" aged {age}")]
    public async Task ThenTheStoredPassengerIs(string name, int age)
    {
        var passenger = (await GetJsonAsync<PassengerRecord>($"/api/passengers/{_passengerId}")).Body;
        SpecAssert.Check("name", passenger?.Name, name);
        SpecAssert.Check("age", passenger?.Age, age);
    }

    [Then("the stored flight is {flightNumber} priced {price}")]
    public async Task ThenTheStoredFlightIs(string flightNumber, decimal price)
    {
        var flight = (await GetJsonAsync<FlightRecord>($"/api/flights/{_flightId}")).Body;
        SpecAssert.Check("flight number", flight?.FlightNumber, flightNumber);
        SpecAssert.Check("price", flight?.Price, price);
    }

    [Then("the flights include {flightNumber}")]
    public void ThenTheFlightsInclude(string flightNumber)
        => SpecAssert.Fact(_flights.Any(f => f.FlightNumber == flightNumber),
            $"The flights are [{string.Join(", ", _flights.Select(f => f.FlightNumber))}], expected '{flightNumber}'.");

    /// <summary>
    /// Reads the booking back over HTTP as well: the GET serves the inline snapshot, and that is what a
    /// client sees.
    /// </summary>
    [Then("the stored booking is for \"{passengerName}\" on flight {flightNumber} priced {price}")]
    public async Task ThenTheStoredBookingIs(string passengerName, string flightNumber, decimal price)
    {
        var booking = (await GetJsonAsync<BookingRecord>($"/api/bookings/{_bookingId}")).Body;
        SpecAssert.Check("passenger", booking?.PassengerName, passengerName);
        SpecAssert.Check("flight number", booking?.FlightNumber, flightNumber);
        SpecAssert.Check("price", booking?.Price, price);
    }

    [Then("the bookings include the new booking")]
    public void ThenTheBookingsIncludeTheNewBooking()
        => SpecAssert.Fact(_bookings.Any(b => b.Id == _bookingId), $"Booking {_bookingId} is not in the list.");
}
