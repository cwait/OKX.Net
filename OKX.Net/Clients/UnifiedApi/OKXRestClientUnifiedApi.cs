﻿using CryptoExchange.Net.Clients;
using CryptoExchange.Net.CommonObjects;
using CryptoExchange.Net.Converters.MessageParsing;
using CryptoExchange.Net.Interfaces.CommonClients;
using CryptoExchange.Net.SharedApis;
using OKX.Net.Interfaces.Clients.UnifiedApi;
using OKX.Net.Objects;
using OKX.Net.Objects.Core;
using OKX.Net.Objects.Options;

namespace OKX.Net.Clients.UnifiedApi;

internal partial class OKXRestClientUnifiedApi : RestApiClient, IOKXRestClientUnifiedApi, ISpotClient
{
    #region Internal Fields
    public new OKXRestOptions ClientOptions => (OKXRestOptions)base.ClientOptions;

    private static TimeSyncState _timeSyncState = new("Unified Api");

    public event Action<OrderId>? OnOrderPlaced;
    public event Action<OrderId>? OnOrderCanceled;
    #endregion

    public IOKXRestClientUnifiedApiAccount Account { get; private set; }
    public IOKXRestClientUnifiedApiExchangeData ExchangeData { get; private set; }
    public IOKXRestClientUnifiedApiTrading Trading { get; private set; }
    public IOKXRestClientUnifiedApiSubAccounts SubAccounts { get; private set; }

    public string ExchangeName => "OKX";

    public ISpotClient CommonSpotClient => this;
    public IOKXRestClientUnifiedApiShared SharedClient => this;

    internal OKXRestClientUnifiedApi(ILogger logger, HttpClient? httpClient, OKXRestOptions options)
            : base(logger, httpClient, options.Environment.RestAddress, options, options.UnifiedOptions)
    {
        Account = new OKXRestClientUnifiedApiAccount(this);
        ExchangeData = new OKXRestClientUnifiedApiExchangeData(this);
        Trading = new OKXRestClientUnifiedApiTrading(this);
        SubAccounts = new OKXRestClientUnifiedApiSubAccounts(this);

        if (options.Environment.Name == TradeEnvironmentNames.Testnet)
        {
            StandardRequestHeaders = new Dictionary<string, string>
            {
                { "x-simulated-trading", "1" }
            };
        }
    }

    /// <inheritdoc />
    protected override IStreamMessageAccessor CreateAccessor() => new SystemTextJsonStreamMessageAccessor();

    /// <inheritdoc />
    protected override IMessageSerializer CreateSerializer() => new SystemTextJsonMessageSerializer();

    /// <inheritdoc />
    protected override AuthenticationProvider CreateAuthenticationProvider(ApiCredentials credentials)
        => new OKXAuthenticationProvider((OKXApiCredentials)credentials);

    /// <inheritdoc />
    public override string FormatSymbol(string baseAsset, string quoteAsset, TradingMode tradingMode, DateTime? deliverTime = null)
        => OKXExchange.FormatSymbol(baseAsset, quoteAsset, tradingMode, deliverTime);

    internal Task<WebCallResult> SendAsync(RequestDefinition definition, ParameterCollection? parameters, CancellationToken cancellationToken, int? weight = null)
           => base.SendAsync(BaseAddress, definition, parameters, cancellationToken, null, weight);

    internal async Task<WebCallResult<T>> SendToAddressAsync<T>(string baseAddress, RequestDefinition definition, ParameterCollection? parameters, CancellationToken cancellationToken, int? weight = null, Dictionary<string, string>? requestHeaders = null) where T : class
    {
        var result = await base.SendAsync<OKXRestApiResponse<T>>(baseAddress, definition, parameters, cancellationToken, requestHeaders, weight).ConfigureAwait(false);
        if (!result.Success) return result.AsError<T>(result.Error!);
        if (result.Data.ErrorCode > 0) return result.AsError<T>(new OKXRestApiError(result.Data.ErrorCode, result.Data.ErrorMessage!, null));

        return result.As<T>(result.Data.Data);
    }

    internal Task<WebCallResult<T>> SendAsync<T>(RequestDefinition definition, ParameterCollection? queryParameters, ParameterCollection? bodyParameters, CancellationToken cancellationToken, int? weight = null) where T : class
        => SendToAddressAsync<T>(BaseAddress, definition, queryParameters, bodyParameters, cancellationToken, weight);

    internal Task<WebCallResult<T>> SendAsync<T>(RequestDefinition definition, ParameterCollection? parameters, CancellationToken cancellationToken, int? weight = null, Dictionary<string, string>? requestHeaders = null) where T : class
        => SendToAddressAsync<T>(BaseAddress, definition, parameters, cancellationToken, weight, requestHeaders);

    internal async Task<WebCallResult<T>> SendRawAsync<T>(RequestDefinition definition, ParameterCollection? parameters, CancellationToken cancellationToken, int? weight = null, Dictionary<string, string>? requestHeaders = null) where T : class
    {
        return await base.SendAsync<T>(BaseAddress, definition, parameters, cancellationToken, null, weight).ConfigureAwait(false);
    }

    internal async Task<WebCallResult<T>> SendGetSingleAsync<T>(RequestDefinition definition, ParameterCollection? parameters, CancellationToken cancellationToken, int? weight = null, Dictionary<string, string>? requestHeaders = null) where T : class
    {
        var result = await SendToAddressAsync<IEnumerable<T>>(BaseAddress, definition, parameters, cancellationToken, weight, requestHeaders).ConfigureAwait(false);
        if (!result)
            return result.As<T>(default);
        
        if (!result.Data.Any())
            return result.AsError<T>(new ServerError("No response data"));

        return result.As<T>(result.Data?.FirstOrDefault());
    }

    internal async Task<WebCallResult<T>> SendToAddressAsync<T>(string baseAddress, RequestDefinition definition, ParameterCollection? queryParameters, ParameterCollection? bodyParameters, CancellationToken cancellationToken, int? weight = null) where T : class
    {
        var result = await base.SendAsync<OKXRestApiResponse<T>>(baseAddress, definition, queryParameters, bodyParameters, cancellationToken, null, weight).ConfigureAwait(false);
        if (!result.Success) return result.AsError<T>(result.Error!);
        if (result.Data.ErrorCode > 0) return result.AsError<T>(new OKXRestApiError(result.Data.ErrorCode, result.Data.ErrorMessage!, null));

        return result.As<T>(result.Data.Data);
    }

    /// <inheritdoc />
    protected override Task<WebCallResult<DateTime>> GetServerTimestampAsync()
        => ExchangeData.GetServerTimeAsync();

    /// <inheritdoc />
    public override TimeSyncInfo? GetTimeSyncInfo()
        => new(_logger, ClientOptions.AutoTimestamp, ClientOptions.TimestampRecalculationInterval, _timeSyncState);

    /// <inheritdoc />
    public override TimeSpan? GetTimeOffset()
        => _timeSyncState.TimeOffset;

    /// <inheritdoc />
    protected override Error ParseErrorResponse(int httpStatusCode, IEnumerable<KeyValuePair<string, IEnumerable<string>>> responseHeaders, IMessageAccessor accessor)
    {
        if (!accessor.IsJson)
            return new ServerError(accessor.GetOriginalString());

        var codePath = MessagePath.Get().Property("code");
        var msgPath = MessagePath.Get().Property("msg");
        var code = accessor.GetValue<string?>(codePath);
        var msg = accessor.GetValue<string>(msgPath);
        if (msg == null)
            return new ServerError(accessor.GetOriginalString());

        if (code == null || !int.TryParse(code, out var intCode))
            return new ServerError(msg);

        return new ServerError(intCode, msg);
    }

    internal void InvokeOrderPlaced(OrderId id)
    {
        OnOrderPlaced?.Invoke(id);
    }

    internal void InvokeOrderCanceled(OrderId id)
    {
        OnOrderCanceled?.Invoke(id);
    }

    async Task<WebCallResult<OrderId>> ISpotClient.PlaceOrderAsync(string symbol, CommonOrderSide side, CommonOrderType type, decimal quantity, decimal? price, string? accountId, string? clientOrderId, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.PlaceOrderAsync), nameof(symbol));

        var orderResult = await Trading.PlaceOrderAsync(
            symbol,
            side == CommonOrderSide.Buy ? Enums.OrderSide.Buy : Enums.OrderSide.Sell,
            type == CommonOrderType.Limit ? Enums.OrderType.Limit : Enums.OrderType.Market,
            quantity,
            price,
            clientOrderId: clientOrderId,
            ct: ct).ConfigureAwait(false);

        if (!orderResult)
            return orderResult.As<OrderId>(default);

        if (orderResult.Data.OrderId == null)
            return orderResult.AsError<OrderId>(new ServerError(orderResult.Data.Message));

        return orderResult.As(new OrderId
        {
            Id = orderResult.Data.OrderId.ToString(),
            SourceObject = orderResult.Data
        });
    }

    public string GetSymbolName(string baseAsset, string quoteAsset) => baseAsset.ToUpperInvariant() + "-" + quoteAsset.ToUpperInvariant();

    async Task<WebCallResult<IEnumerable<Symbol>>> IBaseRestClient.GetSymbolsAsync(CancellationToken ct)
    {
        var symbols = await ExchangeData.GetSymbolsAsync(Enums.InstrumentType.Spot, ct: ct).ConfigureAwait(false);
        if (!symbols)
            return symbols.As<IEnumerable<Symbol>>(default);

        return symbols.As(symbols.Data.Select(s => new Symbol
        {
            Name = s.Symbol,
            MinTradeQuantity = s.MinimumOrderSize,
            PriceStep = s.TickSize,
            SourceObject = s,
            QuantityStep = s.LotSize
        }));
    }

    async Task<WebCallResult<Ticker>> IBaseRestClient.GetTickerAsync(string symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetTickerAsync), nameof(symbol));

        var ticker = await ExchangeData.GetTickerAsync(symbol, ct).ConfigureAwait(false);
        if (!ticker)
            return ticker.As<Ticker>(default);

        return ticker.As(new Ticker
        {
            HighPrice = ticker.Data.HighPrice,
            LastPrice = ticker.Data.LastPrice,
            LowPrice = ticker.Data.LowPrice,
            Price24H = ticker.Data.OpenPrice,
            Symbol = symbol,
            Volume = ticker.Data.Volume,
            SourceObject = ticker
        });
    }

    async Task<WebCallResult<IEnumerable<Ticker>>> IBaseRestClient.GetTickersAsync(CancellationToken ct)
    {
        var tickers = await ExchangeData.GetTickersAsync(Enums.InstrumentType.Spot, ct: ct).ConfigureAwait(false);
        if (!tickers)
            return tickers.As<IEnumerable<Ticker>>(default);

        return tickers.As(tickers.Data.Select(ticker => new Ticker
        {
            HighPrice = ticker.HighPrice,
            LastPrice = ticker.LastPrice,
            LowPrice = ticker.LowPrice,
            Price24H = ticker.OpenPrice,
            Symbol = ticker.Symbol,
            Volume = ticker.Volume,
            SourceObject = ticker
        }));
    }

    async Task<WebCallResult<IEnumerable<Kline>>> IBaseRestClient.GetKlinesAsync(string symbol, TimeSpan timespan, DateTime? startTime, DateTime? endTime, int? limit, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetKlinesAsync), nameof(symbol));

        var seconds = (int)timespan.TotalSeconds;
        var period = (Enums.KlineInterval)seconds;

        if (!Enum.IsDefined(typeof(Enums.KlineInterval), seconds))
            throw new ArgumentException("Unsupported timespan for OKX Klines, check supported intervals using OKX.Net.Enums.OKXPeriod");

        var tickers = await ExchangeData.GetKlinesAsync(symbol, period, startTime, endTime, ct: ct).ConfigureAwait(false);
        if (!tickers)
            return tickers.As<IEnumerable<Kline>>(default);

        return tickers.As(tickers.Data.Select(ticker => new Kline
        {
            HighPrice = ticker.HighPrice,
            ClosePrice = ticker.ClosePrice,
            LowPrice = ticker.LowPrice,
            OpenPrice = ticker.OpenPrice,
            OpenTime = ticker.Time,
            Volume = ticker.Volume,
            SourceObject = ticker
        }));
    }

    async Task<WebCallResult<OrderBook>> IBaseRestClient.GetOrderBookAsync(string symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetOrderBookAsync), nameof(symbol));

        var book = await ExchangeData.GetOrderBookAsync(symbol, 25, ct).ConfigureAwait(false);
        if (!book)
            return book.As<OrderBook>(default);

        return book.As(new OrderBook
        {
            Asks = book.Data.Asks.Select(a => new OrderBookEntry { Price = a.Price, Quantity = a.Quantity }),
            Bids = book.Data.Bids.Select(a => new OrderBookEntry { Price = a.Price, Quantity = a.Quantity }),
            SourceObject = book
        });
    }

    async Task<WebCallResult<IEnumerable<Trade>>> IBaseRestClient.GetRecentTradesAsync(string symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetRecentTradesAsync), nameof(symbol));

        var trades = await ExchangeData.GetRecentTradesAsync(symbol, ct: ct).ConfigureAwait(false);
        if (!trades)
            return trades.As<IEnumerable<Trade>>(default);

        return trades.As(trades.Data.Select(trade => new Trade
        {
            Quantity = trade.Quantity,
            Price = trade.Price,
            Timestamp = trade.Time,
            Symbol = trade.Symbol,
            SourceObject = trade
        }));
    }

    async Task<WebCallResult<IEnumerable<Balance>>> IBaseRestClient.GetBalancesAsync(string? accountId, CancellationToken ct)
    {
        var balances = await Account.GetFundingBalanceAsync(ct: ct).ConfigureAwait(false);
        if (!balances)
            return balances.As<IEnumerable<Balance>>(default);

        return balances.As(balances.Data.Select(balance => new Balance
        {
            Asset = balance.Asset,
            Available = balance.Available,
            Total = balance.Frozen + balance.Available,
            SourceObject = balance
        }));
    }

    async Task<WebCallResult<Order>> IBaseRestClient.GetOrderAsync(string orderId, string? symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetOrderAsync), nameof(symbol));

        if (!long.TryParse(orderId, out var longId))
            throw new ArgumentException(nameof(orderId) + " is not a valid id for OKX " + nameof(ISpotClient.GetOrderAsync), nameof(orderId));

        var orderResult = await Trading.GetOrderDetailsAsync(symbol, longId, ct: ct).ConfigureAwait(false);
        if (!orderResult)
            return orderResult.As<Order>(default);

        return orderResult.As(new Order
        {
            Id = orderId,
            Price = orderResult.Data.Price,
            Quantity = orderResult.Data.Quantity,
            QuantityFilled = orderResult.Data.QuantityFilled,
            Timestamp = orderResult.Data.CreateTime,
            Symbol = orderResult.Data.Symbol,
            Status = orderResult.Data.OrderState == Enums.OrderStatus.Canceled ? CommonOrderStatus.Canceled :
                     orderResult.Data.OrderState == Enums.OrderStatus.Filled ? CommonOrderStatus.Filled :
                     CommonOrderStatus.Filled,
            Side = orderResult.Data.OrderSide == Enums.OrderSide.Sell ? CommonOrderSide.Sell: CommonOrderSide.Buy,
            Type = orderResult.Data.OrderType == Enums.OrderType.Limit ? CommonOrderType.Limit
                 : orderResult.Data.OrderType == Enums.OrderType.Market ? CommonOrderType.Market:
                  CommonOrderType.Other,
            SourceObject = orderResult.Data
        });
    }

    async Task<WebCallResult<IEnumerable<UserTrade>>> IBaseRestClient.GetOrderTradesAsync(string orderId, string? symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetOrderAsync), nameof(symbol));

        if (!long.TryParse(orderId, out var longId))
            throw new ArgumentException(nameof(orderId) + " is not a valid id for OKX " + nameof(ISpotClient.GetOrderAsync), nameof(orderId));

        var tradesResult = await Trading.GetUserTradesAsync(Enums.InstrumentType.Spot, orderId: longId, ct: ct).ConfigureAwait(false);
        if (!tradesResult)
            return tradesResult.As<IEnumerable<UserTrade>>(default);

        return tradesResult.As(tradesResult.Data.Select(x => new UserTrade
        {
            Fee = x.Fee,
            FeeAsset = x.FeeAsset,
            Id = x.TradeId.ToString(),
            OrderId = orderId,
            Price = x.FillPrice ?? 0,
            Quantity = x.QuantityFilled ?? 0,
            Symbol = x.Symbol,
            Timestamp = x.FillTime
        }));
    }

    async Task<WebCallResult<IEnumerable<Order>>> IBaseRestClient.GetOpenOrdersAsync(string? symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetOpenOrdersAsync), nameof(symbol));

        var ordersResult = await Trading.GetOrdersAsync(Enums.InstrumentType.Spot, symbol, ct: ct).ConfigureAwait(false);
        if (!ordersResult)
            return ordersResult.As<IEnumerable<Order>>(default);

        return ordersResult.As(ordersResult.Data.Select( x => new Order
        {
            Id = x.OrderId.ToString(),
            Price = x.Price,
            Quantity = x.Quantity,
            QuantityFilled = x.QuantityFilled,
            Timestamp = x.CreateTime,
            Symbol = x.Symbol,
            Status = x.OrderState == Enums.OrderStatus.Canceled ? CommonOrderStatus.Canceled :
                     x.OrderState == Enums.OrderStatus.Filled ? CommonOrderStatus.Filled :
                     CommonOrderStatus.Filled,
            Side = x.OrderSide == Enums.OrderSide.Sell ? CommonOrderSide.Sell : CommonOrderSide.Buy,
            Type = x.OrderType == Enums.OrderType.Limit ? CommonOrderType.Limit
                 : x.OrderType == Enums.OrderType.Market ? CommonOrderType.Market :
                  CommonOrderType.Other,
            SourceObject = x
        }));
    }

    async Task<WebCallResult<IEnumerable<Order>>> IBaseRestClient.GetClosedOrdersAsync(string? symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.GetClosedOrdersAsync), nameof(symbol));

        var ordersResult = await Trading.GetOrderHistoryAsync(Enums.InstrumentType.Spot, symbol, ct: ct).ConfigureAwait(false);
        if (!ordersResult)
            return ordersResult.As<IEnumerable<Order>>(default);

        return ordersResult.As(ordersResult.Data.Select(x => new Order
        {
            Id = x.OrderId.ToString(),
            Price = x.Price,
            Quantity = x.Quantity,
            QuantityFilled = x.QuantityFilled,
            Timestamp = x.CreateTime,
            Symbol = x.Symbol,
            Status = x.OrderState == Enums.OrderStatus.Canceled ? CommonOrderStatus.Canceled :
                     x.OrderState == Enums.OrderStatus.Filled ? CommonOrderStatus.Filled :
                     CommonOrderStatus.Filled,
            Side = x.OrderSide == Enums.OrderSide.Sell ? CommonOrderSide.Sell : CommonOrderSide.Buy,
            Type = x.OrderType == Enums.OrderType.Limit ? CommonOrderType.Limit
                 : x.OrderType == Enums.OrderType.Market ? CommonOrderType.Market :
                  CommonOrderType.Other,
            SourceObject = x
        }));
    }

    async Task<WebCallResult<OrderId>> IBaseRestClient.CancelOrderAsync(string orderId, string? symbol, CancellationToken ct)
    {
        if (symbol == null)
            throw new ArgumentException(nameof(symbol) + " required for OKX " + nameof(ISpotClient.CancelOrderAsync), nameof(symbol));

        if (!long.TryParse(orderId, out var longId))
            throw new ArgumentException(nameof(orderId) + " is not a valid id for OKX " + nameof(ISpotClient.CancelOrderAsync), nameof(orderId));

        var ordersResult = await Trading.CancelOrderAsync(symbol, longId, ct: ct).ConfigureAwait(false);
        if (!ordersResult)
            return ordersResult.As<OrderId>(default);

        return ordersResult.As(new OrderId
        {
            Id = orderId,
            SourceObject = ordersResult.Data
        });
    }
}
