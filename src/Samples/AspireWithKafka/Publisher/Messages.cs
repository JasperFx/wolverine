namespace AspireWithKafka;

public record OrderPlaced(Guid OrderId, string ProductCode, int Quantity);
