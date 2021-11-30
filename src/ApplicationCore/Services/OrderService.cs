using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Microsoft.Extensions.Options;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;
    private readonly ServiceBusOptions _serviceBusOptions;
    private readonly AzureFunctionOptions _azureFunctionOptions;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer,
        IOptions<ServiceBusOptions> serviceBusOptions,
        IOptions<AzureFunctionOptions> azureFunctionOptions)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
        _serviceBusOptions = serviceBusOptions.Value;
        _azureFunctionOptions = azureFunctionOptions.Value;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        Guard.Against.NullBasket(basketId, basket);
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await _orderRepository.AddAsync(order);

        await PublishOrder(order);
        await PublishDelivery(order);
    }

    private async Task PublishOrder(Order order)
    {
        var content = new
        {
            OrderId = order.Id,
            Items = order.OrderItems.Select(i => new { i.Id, i.Units })
        };

        await using var client = new ServiceBusClient(_serviceBusOptions.ConnectionString);
        var sender = client.CreateSender("reservations");
        var message = new ServiceBusMessage(JsonSerializer.Serialize(content));
        await sender.SendMessageAsync(message);
    }

    private async Task PublishDelivery(Order order)
    {
        var client = new HttpClient();

        var content = JsonContent.Create(new
        {
            OrderId = order.Id,
            Address = order.ShipToAddress,
            Items = order.OrderItems.Select(i => new { i.Id, i.Units })
        });
              
        await client.PostAsync(new Uri(_azureFunctionOptions.Url), content);
    }
}
