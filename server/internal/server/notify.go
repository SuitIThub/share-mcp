package server

import (
	"context"
	"errors"
	"time"

	firebase "firebase.google.com/go/v4"
	"firebase.google.com/go/v4/messaging"
	"google.golang.org/api/option"
)

// Notifier wakes Android devices via push.
type Notifier interface {
	Notify(ctx context.Context, fcmToken string, data map[string]string) error
}

// ErrTokenGone signals the push token is no longer valid and should be cleared.
var ErrTokenGone = errors.New("push token no longer registered")

type NopNotifier struct{}

func (NopNotifier) Notify(context.Context, string, map[string]string) error { return nil }

type fcmNotifier struct {
	client *messaging.Client
}

// NewFCMNotifier creates a Firebase Cloud Messaging notifier from a service account JSON file.
func NewFCMNotifier(ctx context.Context, credentialsPath string) (Notifier, error) {
	app, err := firebase.NewApp(ctx, nil, option.WithCredentialsFile(credentialsPath))
	if err != nil {
		return nil, err
	}
	c, err := app.Messaging(ctx)
	if err != nil {
		return nil, err
	}
	return &fcmNotifier{client: c}, nil
}

func fcmMessage(token string, data map[string]string) *messaging.Message {
	ttl := 10 * time.Minute
	return &messaging.Message{
		Token:   token,
		Data:    data,
		Android: &messaging.AndroidConfig{Priority: "high", TTL: &ttl},
	}
}

func (f *fcmNotifier) Notify(ctx context.Context, token string, data map[string]string) error {
	_, err := f.client.Send(ctx, fcmMessage(token, data))
	if err != nil && messaging.IsUnregistered(err) {
		return ErrTokenGone
	}
	return err
}
