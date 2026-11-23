package server

import (
	"context"
	"errors"
)

// Notifier wakes Android devices via push.
type Notifier interface {
	Notify(ctx context.Context, fcmToken string, data map[string]string) error
}

// ErrTokenGone signals the push token is no longer valid and should be cleared.
var ErrTokenGone = errors.New("push token no longer registered")

type NopNotifier struct{}

func (NopNotifier) Notify(context.Context, string, map[string]string) error { return nil }
