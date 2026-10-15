package legacy

type Cart struct{}

func (c *Cart) Add(name string, n int) {}

func NewStore() int { return 0 }

func setup() string { return "" }

func Pad(s string) string { return s }