using Commerce.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commerce.Infrastructure;

public static class CatalogSeed
{
    public static async Task InitializeAsync(CommerceDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);
        var existing = await db.Products.Select(p => p.Id).ToHashSetAsync(cancellationToken);
        var entries = new (string Name, string Category, decimal Price, string Artwork, string Color, string Description)[]
        {
            ("Forma Wireless", "Audio", 189, "headphones", "sage", "A quieter kind of everyday. Over-ear wireless headphones with active noise cancellation, soft memory-foam cushions, and up to 40 hours of listening."),
            ("Tempo Mechanical", "Workspace", 129, "keyboard", "sand", "Make every keystroke count. A compact 75% mechanical keyboard with tactile switches, warm backlighting, and wireless connectivity."),
            ("Pebble Mini", "Audio", 69, "speaker", "peach", "Small footprint. Room-filling sound. A portable Bluetooth speaker with a woven finish, splash resistance, and 12-hour battery life."),
            ("Arc Desk Light", "Workspace", 89, "lamp", "lavender", "Better light for your best ideas. An adjustable desk lamp with three color temperatures, smooth dimming, and a weighted aluminum base."),
            ("Orbit Pro", "Workspace", 79, "mouse", "sage", "Find your flow. An ergonomic wireless mouse with quiet switches, a precision sensor, and easy switching between three devices."),
            ("Pocket Power", "Everyday", 59, "powerbank", "sand", "A little backup for the long way home. A 10,000 mAh power bank with USB-C fast charging and a clear charge indicator."),
            ("Pulse Studio", "Gaming", 149, "headphones", "lavender", "Stay in the moment. A lightweight gaming headset with a detachable microphone, detailed stereo sound, and breathable ear cushions."),
            ("Carry Daily", "Everyday", 95, "bag", "peach", "Room for what matters. A weather-resistant everyday backpack with a padded 16-inch laptop sleeve and considered internal organization."),
            ("Echo Buds", "Audio", 99, "earbuds", "sand", "Pocket-sized wireless earbuds with a comfortable fit, clear calls, and a compact USB-C charging case."),
            ("Studio One", "Audio", 249, "headphones", "lavender", "Wired over-ear headphones designed for focused listening, with replaceable cushions and a detachable cable."),
            ("Wave Room", "Audio", 159, "speaker", "sage", "A home Bluetooth speaker with balanced stereo sound, tactile controls, and a fabric-covered enclosure."),
            ("Air Clip", "Audio", 89, "earbuds", "peach", "Light open-fit earbuds for walks and daily listening, with physical controls and a charging case."),
            ("Tone Compact", "Audio", 49, "speaker", "sand", "A compact everyday speaker with a carrying loop, simple controls, and a rechargeable battery."),
            ("Listen Air", "Audio", 139, "headphones", "sage", "Lightweight wireless headphones with fold-flat earcups and a soft travel pouch."),
            ("Note Slim", "Workspace", 79, "keyboard", "lavender", "A low-profile wireless keyboard for quieter work, with a full row of shortcuts and multi-device pairing."),
            ("Glide Silent", "Workspace", 49, "mouse", "peach", "A compact rechargeable mouse with silent clicks and adjustable sensitivity for everyday work."),
            ("Halo Task", "Workspace", 119, "lamp", "sand", "A dimmable task lamp with a wide light spread, touch controls, and a memory setting."),
            ("Dock Nine", "Workspace", 109, "powerbank", "sage", "A nine-port USB-C hub with HDMI, card readers, USB ports, and pass-through charging."),
            ("Write Mini", "Workspace", 99, "keyboard", "peach", "A compact mechanical keyboard with a detachable USB-C cable and hot-swappable switches."),
            ("Beam Portable", "Workspace", 69, "lamp", "sage", "A cordless desk light with a rechargeable battery and a gently diffused reading mode."),
            ("Daypack Lite", "Everyday", 79, "bag", "sand", "A lightweight daypack with a water-resistant shell, external bottle pocket, and padded shoulder straps."),
            ("Charge Duo", "Everyday", 39, "powerbank", "lavender", "A compact dual-port USB-C wall charger for phones, tablets, and everyday accessories."),
            ("Weekender", "Everyday", 129, "bag", "sage", "A versatile travel bag with a wide opening, a laptop compartment, and a luggage pass-through."),
            ("Power Slim", "Everyday", 45, "powerbank", "peach", "A slim 5,000 mAh portable battery with a single USB-C port and a pocket-friendly shape."),
            ("Metro Sling", "Everyday", 55, "bag", "lavender", "A small crossbody sling with an adjustable strap and separate pockets for daily essentials."),
            ("Travel Max", "Everyday", 89, "powerbank", "sand", "A 20,000 mAh power bank with dual-device charging and a simple digital battery display."),
            ("Quest Controller", "Gaming", 69, "controller", "sage", "A wireless controller with textured grips, remappable rear buttons, and USB-C charging."),
            ("Vector Keys", "Gaming", 159, "keyboard", "lavender", "A responsive mechanical gaming keyboard with linear switches and configurable per-key lighting."),
            ("Swift Precision", "Gaming", 89, "mouse", "sand", "A lightweight gaming mouse with a precise optical sensor, flexible cable, and programmable buttons."),
            ("Nova Controller", "Gaming", 89, "controller", "peach", "A comfortable wireless controller with adjustable triggers and multi-platform connectivity."),
            ("Arena Wireless", "Gaming", 179, "headphones", "sage", "A wireless gaming headset with low-latency connectivity, a flip-to-mute microphone, and a cushioned headband."),
            ("Dash Mini", "Gaming", 59, "mouse", "lavender", "A compact gaming mouse with six programmable buttons and onboard profile storage.")
        };
        for (var index = 0; index < entries.Length; index++)
        {
            var item = entries[index];
            var id = Guid.Parse($"10000000-0000-0000-0000-{index + 1:D12}");
            if (existing.Contains(id)) continue;
            db.Products.Add(new Product
            {
                Id = id, Slug = item.Name.ToLowerInvariant().Replace(' ', '-'), Name = item.Name,
                Category = item.Category, Price = item.Price, Artwork = item.Artwork, Color = item.Color,
                Description = item.Description, Stock = index == 30 ? 0 : 8 + index * 3,
                Badge = index == 0 ? "The everyday favorite" : index == 2 ? "Small but mighty" : index == 3 ? "New arrival" : null,
                DisplayOrder = index
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
