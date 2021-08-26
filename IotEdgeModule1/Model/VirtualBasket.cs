using System.Collections.Generic;

namespace IotEdgeModule1.Model
{
    public class VirtualBasket
    {
        public List<BasketProduct> BasketProducts { get; set; } = new List<BasketProduct>();

        public class BasketProduct
        {
            public string Stockcode { get; set; }
            public string ProductName { get; set; }
            public decimal Quantity { get; set; }
        }
    }
}
