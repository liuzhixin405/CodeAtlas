package hipstershop

type Cart struct {
	UserId string
	Items  []*CartItem
}

type CartItem struct {
	ProductId string
}

func (x *CartItem) GetProductId() string { return x.ProductId }

func (x *Cart) GetItems() []*CartItem { return x.Items }

type CartServiceClient interface {
	GetCart(id string) (*Cart, error)
}

func NewCartServiceClient(conn string) CartServiceClient { return nil }
