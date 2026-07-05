using Microsoft.EntityFrameworkCore;
using SterlingLams.Web.Data;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Infrastructure;

public static class SettingsSeedData
{
    public static async Task SeedAsync(ApplicationDbContext db, ILogger logger)
    {
        var definitions = GetAllSettings();
        var existingKeys = await db.SiteSettings.Select(s => s.Key).ToListAsync();
        var toAdd = definitions.Where(d => !existingKeys.Contains(d.Key)).ToList();

        if (toAdd.Count > 0)
        {
            db.SiteSettings.AddRange(toAdd);
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded {Count} site settings.", toAdd.Count);
        }

        // One-time migrations: update stale defaults / types
        var announcementColor = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == "announcement.bg_color");
        if (announcementColor != null && (announcementColor.Value == "bg-neutral-900" || string.IsNullOrWhiteSpace(announcementColor.Value)))
            announcementColor.Value = "bg-brand-500";

        // Ensure hero_image_url uses type "image" so the admin shows a file picker
        var heroImg = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == "homepage.hero_image_url");
        if (heroImg != null && heroImg.Type == "url")
            heroImg.Type = "image";

        // Keep each setting's METADATA (label/help text/type/group/order) in step with the
        // definitions above for already-seeded keys — without touching the admin-edited Value.
        var defByKey = definitions.ToDictionary(d => d.Key);
        foreach (var s in await db.SiteSettings.ToListAsync())
        {
            if (!defByKey.TryGetValue(s.Key, out var def)) continue;
            if (s.Label != def.Label)             s.Label = def.Label;
            if (s.Description != def.Description)  s.Description = def.Description;
            if (s.Type != def.Type)               s.Type = def.Type;
            if (s.Group != def.Group)             s.Group = def.Group;
            if (s.SortOrder != def.SortOrder)     s.SortOrder = def.SortOrder;
        }

        await db.SaveChangesAsync();
    }

    private static List<SiteSetting> GetAllSettings() => new()
    {
        // ── General ──────────────────────────────────────────────────────────
        new() { Key = "general.logo_url",         Group = "General", Label = "Site Logo",          Type = "image",   Value = "",                                               Description = "Upload your logo (or paste a URL). Shown in the footer. Recommended: transparent PNG.", SortOrder = 0 },
        new() { Key = "general.payment_badges_url", Group = "General", Label = "Payment Methods Image", Type = "image", Value = "",                                             Description = "Optional image of accepted/secure payment methods (e.g. Visa, Mastercard, Verve, Paystack). Shown in the footer and at checkout. Leave blank to use the built-in badge strip. Recommended: a wide transparent PNG.", SortOrder = 1 },
        new() { Key = "general.site_name",       Group = "General", Label = "Site Name",          Type = "text",    Value = "Sterlin Glams",                                  Description = "Displayed in the browser tab and emails.",    SortOrder = 1 },
        new() { Key = "general.tagline",          Group = "General", Label = "Tagline",            Type = "text",    Value = "Luxury Jewellery. Timeless Elegance.",           Description = "Short brand tagline used in meta descriptions.", SortOrder = 2 },
        new() { Key = "general.contact_email",    Group = "General", Label = "Contact Email",      Type = "email",   Value = "info@sterlinglams.com",                          Description = "Displayed in the footer and contact page.",   SortOrder = 3 },
        new() { Key = "general.contact_phone",    Group = "General", Label = "Contact Phone",      Type = "tel",     Value = "+234 1 234 5678",                                Description = "Phone number shown to customers.",            SortOrder = 4 },
        new() { Key = "general.whatsapp_number",  Group = "General", Label = "WhatsApp Number",    Type = "tel",     Value = "",                                               Description = "Include country code, e.g. +2348012345678.",  SortOrder = 5 },
        new() { Key = "general.instagram_url",    Group = "General", Label = "Instagram URL",      Type = "url",     Value = "",                                               Description = "Full URL to your Instagram page.",            SortOrder = 6 },
        new() { Key = "general.facebook_url",     Group = "General", Label = "Facebook URL",       Type = "url",     Value = "",                                               Description = "Full URL to your Facebook page.",             SortOrder = 7 },
        new() { Key = "general.tiktok_url",       Group = "General", Label = "TikTok URL",         Type = "url",     Value = "",                                               Description = "Full URL to your TikTok profile.",            SortOrder = 8 },

        // ── Announcement Bar ─────────────────────────────────────────────────
        new() { Key = "announcement.enabled",     Group = "Announcement Bar", Label = "Show Announcement Bar",  Type = "boolean",  Value = "true",  Description = "Toggle the top banner on all pages.",                         SortOrder = 1 },
        new() { Key = "announcement.text",        Group = "Announcement Bar", Label = "Announcement Text",      Type = "text",     Value = "COMPLIMENTARY SHIPPING ON ORDERS OVER ₦150,000  |  IN-STORE PICKUP AVAILABLE", Description = "Text shown in the top banner. Keep it short.", SortOrder = 2 },
        new() { Key = "announcement.bg_color",    Group = "Announcement Bar", Label = "Background Colour",      Type = "text",     Value = "bg-brand-500",    Description = "Tailwind class: bg-brand-500, bg-neutral-900, bg-red-600, bg-emerald-700, etc.", SortOrder = 3 },

        // ── Shipping & Delivery ───────────────────────────────────────────────
        // Lagos & Abuja — Express
        new() { Key = "shipping.lagos_abuja_express_fee",  Group = "Shipping", Label = "Lagos & Abuja Express Fee (N)",         Type = "number", Value = "4000",               Description = "Express delivery fee for Lagos and Abuja FCT.",                SortOrder = 1 },
        new() { Key = "shipping.lagos_abuja_express_days", Group = "Shipping", Label = "Lagos & Abuja Express Timeframe",       Type = "text",   Value = "24 - 48 hours",      Description = "Timeframe shown to customers for express delivery.",           SortOrder = 2 },
        // Lagos & Abuja — Standard
        new() { Key = "shipping.lagos_abuja_standard_fee", Group = "Shipping", Label = "Lagos & Abuja Standard Fee (N)",        Type = "number", Value = "2000",               Description = "Standard delivery fee for Lagos and Abuja FCT.",               SortOrder = 3 },
        new() { Key = "shipping.lagos_abuja_standard_days",Group = "Shipping", Label = "Lagos & Abuja Standard Timeframe",      Type = "text",   Value = "2 - 4 working days", Description = "Timeframe shown to customers for standard delivery.",          SortOrder = 4 },
        // National — Standard
        new() { Key = "shipping.national_standard_fee",   Group = "Shipping", Label = "Nationwide Standard Fee (N)",           Type = "number", Value = "7500",               Description = "Delivery fee for all other Nigerian states.",                  SortOrder = 5 },
        new() { Key = "shipping.national_standard_days",  Group = "Shipping", Label = "Nationwide Standard Timeframe",         Type = "text",   Value = "2 - 5 working days", Description = "Timeframe shown to customers for nationwide standard delivery.", SortOrder = 6 },
        new() { Key = "shipping.cross_branch_days",       Group = "Shipping", Label = "Cross-branch (far stock) Timeframe",   Type = "text",   Value = "3 - 5 working days", Description = "Timeframe shown at checkout when an item must come from a branch far from the customer (e.g. a Lagos buyer ordering Abuja-only stock).", SortOrder = 65 },
        new() { Key = "shipping.returns_policy",           Group = "Shipping", Label = "Shipping & Return Policy (product page)", Type = "textarea", Value = "Orders are processed within 1–2 business days. Delivery timeframes and fees depend on your location (see checkout for details).\n\nReturns are accepted within 7 days of delivery for unworn items in their original packaging. Earrings and custom pieces are non-returnable for hygiene reasons. Contact us to arrange a return.", Description = "Shown in the 'Shipping & Return Policy' tab on every product page.", SortOrder = 7 },

        // ── Notifications ─────────────────────────────────────────────────────
        new() { Key = "notifications.admin_email",     Group = "Notifications", Label = "Admin Email",                    Type = "email",   Value = "rapheal@sterlinglamslogistics.com", Description = "Receives new order and low stock alerts.",         SortOrder = 1 },
        new() { Key = "notifications.new_order",       Group = "Notifications", Label = "New Order Alerts",               Type = "boolean", Value = "true",  Description = "Send email to admin when a new order is placed.",          SortOrder = 2 },
        new() { Key = "notifications.low_stock",       Group = "Notifications", Label = "Low Stock Alerts",               Type = "boolean", Value = "true",  Description = "Send email to admin when stock falls below threshold.",     SortOrder = 3 },
        new() { Key = "notifications.branch_fulfilment", Group = "Notifications", Label = "Branch Fulfilment Alerts",     Type = "boolean", Value = "true",  Description = "Email branches when an online order is fulfilled: each source branch is told to send a transfer, and the fulfilling branch is told to pack & dispatch.", SortOrder = 35 },
        new() { Key = "inventory.low_stock_threshold", Group = "Inventory", Label = "Low Stock Threshold (units)",   Type = "number",  Value = "5",     Description = "At or below this quantity an item counts as low: turns the per-branch availability dot amber on product pages, shows the storefront 'low stock' nudge, and triggers low-stock alerts (the exact number is never shown to customers).", SortOrder = 1 },
        new() { Key = "notifications.order_confirmed", Group = "Notifications", Label = "Customer Order Confirmation",    Type = "boolean", Value = "true",  Description = "Send order confirmation email to customer after payment.",  SortOrder = 4 },
        new() { Key = "notifications.abandoned_cart",       Group = "Notifications", Label = "Abandoned Cart Recovery",        Type = "boolean", Value = "true", Description = "Email shoppers a recovery link if they reach checkout but don't pay.", SortOrder = 5 },
        new() { Key = "notifications.abandoned_cart_hours", Group = "Notifications", Label = "Abandoned Cart Delay (hours)",   Type = "number",  Value = "4",    Description = "Hours after checkout to send the 1st recovery email.",               SortOrder = 6 },
        new() { Key = "notifications.abandoned_cart_hours_2", Group = "Notifications", Label = "Recovery Email 2 Delay (hours)", Type = "number", Value = "24", Description = "2nd reminder, hours after checkout. Set 0 or below the 1st to disable.", SortOrder = 7 },
        new() { Key = "notifications.abandoned_cart_hours_3", Group = "Notifications", Label = "Recovery Email 3 Delay (hours)", Type = "number", Value = "72", Description = "3rd (final) reminder, hours after checkout. Set 0 or below the 2nd to disable.", SortOrder = 8 },
        new() { Key = "notifications.abandoned_cart_discount_pct", Group = "Notifications", Label = "Recovery Discount (%)", Type = "number", Value = "0", Description = "Optional escalating incentive: a unique %-off coupon on the later recovery email(s). 0 = no discount.", SortOrder = 9 },
        new() { Key = "notifications.abandoned_cart_discount_step", Group = "Notifications", Label = "Discount From Email #", Type = "number", Value = "3", Description = "Which recovery email first carries the discount (e.g. 3 = only the final email).", SortOrder = 10 },
        new() { Key = "notifications.abandoned_cart_discount_expiry_days", Group = "Notifications", Label = "Recovery Coupon Expiry (days)", Type = "number", Value = "7", Description = "How long each recovery coupon stays valid.", SortOrder = 11 },

        // ── Welcome popup (exit-intent list growth) ───────────────────────────
        new() { Key = "popup.enabled",      Group = "Marketing", Label = "Welcome Popup",            Type = "boolean",  Value = "false", Description = "Show an exit-intent popup offering a first-order discount for a newsletter signup.", SortOrder = 1 },
        new() { Key = "popup.discount_pct", Group = "Marketing", Label = "Popup Discount (%)",       Type = "number",   Value = "10",    Description = "First-order discount given to new subscribers via the popup. 0 = just collect the email, no coupon.", SortOrder = 2 },
        new() { Key = "popup.headline",     Group = "Marketing", Label = "Popup Headline",           Type = "text",     Value = "Get 10% off your first order", Description = "Main line on the popup.", SortOrder = 3 },
        new() { Key = "popup.subtext",      Group = "Marketing", Label = "Popup Subtext",            Type = "textarea", Value = "Join our list for early access to new pieces and exclusive offers.", Description = "Line under the headline.", SortOrder = 4 },
        new() { Key = "popup.min_order",    Group = "Marketing", Label = "Popup Coupon Min Order (₦)", Type = "number", Value = "0",     Description = "Minimum order value for the popup coupon. 0 = no minimum.", SortOrder = 5 },
        new() { Key = "popup.expiry_days",  Group = "Marketing", Label = "Popup Coupon Expiry (days)", Type = "number", Value = "14",    Description = "How long the welcome coupon stays valid.", SortOrder = 6 },

        // ── Inventory ─────────────────────────────────────────────────────────
        new() { Key = "inventory.show_low_stock_nudge", Group = "Inventory", Label = "Show \"Low stock\" Nudge", Type = "boolean", Value = "true", Description = "Show a 'Low stock — order soon' nudge on product pages when an item is at/below the threshold.", SortOrder = 2 },
        new() { Key = "storefront.hide_out_of_stock", Group = "Inventory", Label = "Hide out-of-stock items on storefront", Type = "boolean", Value = "false", Description = "When on, products with no stock anywhere are hidden from the shop and category pages (a direct link returns Not Found). For variant products, only the in-stock options (size/colour) are shown and sold-out ones are hidden; the product is hidden only when every option is out.", SortOrder = 3 },

        // ── Orders ────────────────────────────────────────────────────────────
        new() { Key = "order.number_prefix",               Group = "Orders", Label = "Online Order Number Prefix",     Type = "text",   Value = "SL-",  Description = "Prefix for online order numbers. Numbers are short and sequential, e.g. SL-30012.", SortOrder = 1 },
        new() { Key = "order.pos_number_prefix",           Group = "Orders", Label = "POS Order Number Prefix",        Type = "text",   Value = "POS-", Description = "Prefix for POS sale numbers. Shares one running counter with online orders, e.g. POS-30013.", SortOrder = 2 },
        new() { Key = "order.reservation_timeout_minutes", Group = "Orders", Label = "Auto-cancel unpaid orders after (minutes)",    Type = "number", Value = "60",  Description = "An unpaid online order is automatically cancelled this many minutes after it's placed if the customer hasn't paid. Stock is only deducted on payment (or when staff confirm), so nothing is held in the meantime.", SortOrder = 2 },
        new() { Key = "order.min_value",                   Group = "Orders", Label = "Minimum Order Value (₦)",        Type = "number", Value = "0",   Description = "Reject online checkout below this subtotal. 0 = no minimum.", SortOrder = 3 },

        // ── POS / Till ────────────────────────────────────────────────────────
        new() { Key = "pos.receipt_header", Group = "POS / Till", Label = "Receipt Header Line", Type = "text",     Value = "",                                  Description = "Extra line printed at the top of POS receipts (e.g. a slogan or address). Blank = none.", SortOrder = 1 },
        new() { Key = "pos.receipt_footer", Group = "POS / Till", Label = "Receipt Footer Line", Type = "textarea", Value = "Thank you for shopping with us!",    Description = "Message printed at the bottom of POS receipts.", SortOrder = 2 },

        // ── Security ──────────────────────────────────────────────────────────
        new() { Key = "security.require_email_confirmation", Group = "Security", Label = "Require Email Confirmation to Sign In", Type = "boolean", Value = "false", Description = "When on, customers must confirm their email before they can sign in. Leave off until existing users are confirmed and SMTP is live.", SortOrder = 1 },

        // ── Loyalty ───────────────────────────────────────────────────────────
        new() { Key = "loyalty.enabled",         Group = "Loyalty", Label = "Enable Loyalty Points",     Type = "boolean", Value = "true", Description = "Award points to customers on completed orders.",                         SortOrder = 1 },
        new() { Key = "loyalty.naira_per_point",  Group = "Loyalty", Label = "Naira per Point (₦)",       Type = "number",  Value = "100",  Description = "How much a customer spends to earn 1 point (e.g. 100 = 1 point per ₦100).", SortOrder = 2 },
        new() { Key = "loyalty.point_value",        Group = "Loyalty", Label = "Point Value on Redemption (₦)",          Type = "number",  Value = "1",    Description = "₦ discount per point when redeemed at checkout (e.g. 1 = 1 point is worth ₦1).", SortOrder = 3 },
        new() { Key = "loyalty.redemption_enabled", Group = "Loyalty", Label = "Allow Redeeming Points at Checkout",     Type = "boolean", Value = "true", Description = "Let signed-in customers apply their points for a discount at checkout.",        SortOrder = 4 },

        // ── Gift cards ─────────────────────────────────────────────────────────
        new() { Key = "giftcards.enabled", Group = "Gift Cards", Label = "Enable Gift Card Redemption", Type = "boolean", Value = "true", Description = "Let customers apply a gift card code at checkout. Issue cards from Admin → Gift Cards.", SortOrder = 1 },

        // ── Referral programme ───────────────────────────────────────────────────
        new() { Key = "referral.enabled",         Group = "Referrals", Label = "Enable Refer-a-Friend",      Type = "boolean", Value = "true", Description = "Give customers a referral link; reward both sides when a referred friend's first order is paid.", SortOrder = 1 },
        new() { Key = "referral.referrer_points", Group = "Referrals", Label = "Points to the Referrer",     Type = "number",  Value = "100",  Description = "Loyalty points the referrer earns per successful referral.", SortOrder = 2 },
        new() { Key = "referral.referee_points",  Group = "Referrals", Label = "Points to the New Customer", Type = "number",  Value = "50",   Description = "Loyalty points the referred friend earns on their first paid order.", SortOrder = 3 },

        // ── Emails (customizer) ───────────────────────────────────────────────
        // Branding (shared by every email)
        new() { Key = "email.from_name",    Group = "Emails", Label = "Sender Name",          Type = "text",     Value = "Sterlin Glams", Description = "Display name on the From line of every email.",            SortOrder = 1 },
        new() { Key = "email.reply_to",     Group = "Emails", Label = "Reply-To Address",     Type = "email",    Value = "",              Description = "Where customer replies go (optional). Blank = no reply-to.", SortOrder = 2 },
        new() { Key = "email.header_color", Group = "Emails", Label = "Header Colour",         Type = "color",    Value = "#0a0a0a",       Description = "Background colour of the email header band.",             SortOrder = 3 },
        new() { Key = "email.footer_text",  Group = "Emails", Label = "Footer Text",           Type = "textarea", Value = "This is an automated message — please don't reply.", Description = "Small print at the bottom of every email.", SortOrder = 4 },
        new() { Key = "email.logo_height",  Group = "Emails", Label = "Logo Size (px)",         Type = "number",   Value = "48",            Description = "Height of the logo in the email header (16–200px).",      SortOrder = 5 },

        // Per-email subject + intro (structure/links stay code-controlled)
        new() { Key = "email.order_confirmed.subject", Group = "Emails", Label = "Order Confirmation — Subject", Type = "text",     Value = "Your order is being processed", Description = "Customer order confirmation (also the heading).",          SortOrder = 10 },
        new() { Key = "email.order_confirmed.intro",   Group = "Emails", Label = "Order Confirmation — Intro",   Type = "textarea", Value = "Your order {order} ({date}) has been received and is now being processed.", Description = "Opening line above the order details. Placeholders: {order}, {date}, {name}.", SortOrder = 11 },
        new() { Key = "email.password_reset.subject",  Group = "Emails", Label = "Password Reset — Subject",     Type = "text",     Value = "Reset your password", Description = "Password reset email.",                                     SortOrder = 12 },
        new() { Key = "email.password_reset.intro",    Group = "Emails", Label = "Password Reset — Intro",       Type = "textarea", Value = "We received a request to reset your password. Click below to choose a new one. This link expires shortly.", Description = "Text above the reset button.", SortOrder = 13 },
        new() { Key = "email.email_confirm.subject",   Group = "Emails", Label = "Email Confirmation — Subject", Type = "text",     Value = "Confirm your email", Description = "Sent after registration.",                                   SortOrder = 14 },
        new() { Key = "email.email_confirm.intro",     Group = "Emails", Label = "Email Confirmation — Intro",   Type = "textarea", Value = "Thanks for creating an account with us. Please confirm this is your email address by clicking below.", Description = "Text above the confirm button.", SortOrder = 15 },
        new() { Key = "email.back_in_stock.subject",   Group = "Emails", Label = "Back-in-Stock — Subject",      Type = "text",     Value = "Good news — it's back in stock", Description = "Sent when a watched item returns.",               SortOrder = 16 },
        new() { Key = "email.back_in_stock.intro",     Group = "Emails", Label = "Back-in-Stock — Intro",        Type = "textarea", Value = "An item you wanted is available again. These pieces sell quickly, so don't wait.", Description = "Opening line (the product name is added below).", SortOrder = 17 },
        new() { Key = "email.abandoned_cart.subject",  Group = "Emails", Label = "Abandoned Cart — Subject",     Type = "text",     Value = "You left something in your bag", Description = "Cart recovery email.",                            SortOrder = 18 },
        new() { Key = "email.abandoned_cart.intro",    Group = "Emails", Label = "Abandoned Cart — Intro",       Type = "textarea", Value = "You have items waiting in your bag — we've saved them for you.", Description = "Opening line above the recovery button.", SortOrder = 19 },

        // ── Store Operations ──────────────────────────────────────────────────
        new() { Key = "store.accepting_orders",    Group = "Store", Label = "Accepting Orders",         Type = "boolean", Value = "true",  Description = "Turn off to temporarily stop customers from placing orders.",          SortOrder = 1 },
        new() { Key = "store.maintenance_mode",    Group = "Store", Label = "Maintenance Mode",         Type = "boolean", Value = "false", Description = "Shows a maintenance page to all visitors (admin still works).",        SortOrder = 2 },
        new() { Key = "store.out_of_stock_msg",   Group = "Store", Label = "Out of Stock Message",     Type = "text",    Value = "This item is currently out of stock. Check back soon.", Description = "Shown on product pages when stock is 0.", SortOrder = 3 },
        new() { Key = "store.pickup_available",    Group = "Store", Label = "In-Store Pickup Available",Type = "boolean", Value = "true",  Description = "Allow customers to choose store pickup at checkout.",                   SortOrder = 4 },
        new() { Key = "store.currency_symbol",     Group = "Store", Label = "Currency Symbol",          Type = "text",    Value = "N",     Description = "Symbol shown next to prices (e.g. N, $, PS).",                          SortOrder = 5 },

        // ── Homepage ──────────────────────────────────────────────────────────
        new() { Key = "homepage.hero_eyebrow",     Group = "Homepage", Label = "Hero Eyebrow (small text above headline)", Type = "text", Value = "New Collection 2025",                          Description = "The small uppercase line above the hero headline (e.g. 'New Collection 2025').", SortOrder = 0 },
        new() { Key = "homepage.hero_headline",    Group = "Homepage", Label = "Hero Headline",          Type = "text",    Value = "Timeless Elegance",                                    Description = "Large text on the homepage hero section.",                              SortOrder = 1 },
        new() { Key = "homepage.hero_subtext",     Group = "Homepage", Label = "Hero Subtext",           Type = "text",    Value = "Luxury jewellery crafted for the discerning woman. Discover our newest arrivals.", Description = "Smaller text below the hero headline.", SortOrder = 2 },
        new() { Key = "homepage.hero_cta",         Group = "Homepage", Label = "Hero Button Text",       Type = "text",    Value = "Shop Collection",                                      Description = "Text on the main call-to-action button.",                               SortOrder = 3 },
        new() { Key = "homepage.hero_image_url",   Group = "Homepage", Label = "Hero Campaign Image URL",Type = "image",   Value = "",                                                     Description = "Upload a campaign photo (or paste a URL). Replaces the hero gradient background. Recommended: full-width landscape, at least 1920×1080px.", SortOrder = 4 },
        new() { Key = "homepage.hero_images",      Group = "Homepage", Label = "Hero Campaign Slider Images", Type = "imagelist", Value = "",                                              Description = "Add multiple campaign photos to make the hero a rotating slider. When set, these replace the single image above. Recommended: full-width landscape, at least 1920×1080px each.", SortOrder = 5 },
        new() { Key = "homepage.hero_slide_seconds", Group = "Homepage", Label = "Hero Slider Speed (seconds)", Type = "number", Value = "5",                                            Description = "How long each hero campaign image shows before sliding to the next. Minimum 2 seconds.", SortOrder = 6 },
        new() { Key = "homepage.featured_slide_seconds", Group = "Homepage", Label = "Featured Slider Speed (seconds)", Type = "number", Value = "3",                                    Description = "How often the Featured Pieces carousel advances to the next card. Minimum 2 seconds.", SortOrder = 7 },
        new() { Key = "homepage.show_featured",    Group = "Homepage", Label = "Show Featured Pieces",   Type = "boolean", Value = "true",                                                 Description = "Show the Featured Pieces section on the homepage.",                     SortOrder = 5 },
        new() { Key = "homepage.show_best_sellers", Group = "Homepage", Label = "Show Best Sellers",      Type = "boolean", Value = "true",                                                 Description = "Show the Best Sellers row on the homepage.",                            SortOrder = 12 },
        new() { Key = "homepage.show_trending",    Group = "Homepage", Label = "Show Trending Now",      Type = "boolean", Value = "true",                                                 Description = "Show the Trending Now row on the homepage.",                            SortOrder = 13 },
        new() { Key = "homepage.show_recently_viewed", Group = "Homepage", Label = "Show Recently Viewed", Type = "boolean", Value = "true",                                              Description = "Show the Recently Viewed row on the homepage (only appears once a visitor has viewed products).", SortOrder = 14 },

        // ── Reviews ─────────────────────────────────────────────────────────────
        new() { Key = "reviews.enabled",      Group = "Reviews", Label = "Enable Product Reviews", Type = "boolean", Value = "true",  Description = "Show ratings & reviews on product pages and let signed-in customers submit them.", SortOrder = 1 },
        new() { Key = "reviews.auto_approve", Group = "Reviews", Label = "Auto-approve Reviews",   Type = "boolean", Value = "false", Description = "Publish new reviews immediately. When off, reviews wait for approval in Admin → Reviews.", SortOrder = 2 },
        new() { Key = "homepage.featured_heading", Group = "Homepage", Label = "Featured Section Heading",Type = "text",  Value = "Featured Pieces",                                      Description = "Heading for the featured products section.",                             SortOrder = 6 },
        new() { Key = "homepage.store_banner_text",Group = "Homepage", Label = "Store Banner Subtext",   Type = "text",   Value = "Experience our jewellery in person at any of our three Lagos boutiques.", Description = "Text in the dark store-finder banner.",              SortOrder = 7 },
        new() { Key = "homepage.trust_1",          Group = "Homepage", Label = "Trust Bar — Item 1",     Type = "text",   Value = "Certified Authentic", Description = "First label in the scrolling pink trust bar under the hero.",  SortOrder = 8 },
        new() { Key = "homepage.trust_2",          Group = "Homepage", Label = "Trust Bar — Item 2",     Type = "text",   Value = "Secure Checkout",     Description = "Second label in the scrolling pink trust bar.",                SortOrder = 9 },
        new() { Key = "homepage.trust_3",          Group = "Homepage", Label = "Trust Bar — Item 3",     Type = "text",   Value = "Easy Returns",        Description = "Third label in the scrolling pink trust bar.",                 SortOrder = 10 },

        // ── Content pages (leave blank to keep the built-in page; paste HTML to replace the body) ──
        new() { Key = "pages.about_body",           Group = "Content Pages", Label = "Our Story (body)",       Type = "html", Value = "", Description = "Replace the 'Our Story' page body. Leave blank to keep the built-in page. Use the toolbar to format text — bold, lists, alignment, fonts.", SortOrder = 1 },
        new() { Key = "pages.privacy_body",         Group = "Content Pages", Label = "Privacy Policy (body)",  Type = "html", Value = "", Description = "Replace the Privacy Policy body. Leave blank to keep the built-in page.", SortOrder = 2 },
        new() { Key = "pages.terms_body",           Group = "Content Pages", Label = "Terms of Service (body)",Type = "html", Value = "", Description = "Replace the Terms of Service body. Leave blank to keep the built-in page.", SortOrder = 3 },
        new() { Key = "pages.payment_returns_body", Group = "Content Pages", Label = "Payment & Returns (body)",Type = "html", Value = "", Description = "Replace the Payment & Returns body. Leave blank to keep the built-in page.", SortOrder = 4 },

        // ── Collections (Lookbook) card images — leave blank for the plain coloured card ──
        new() { Key = "collections.image_rings",     Group = "Collections", Label = "Rings card image",     Type = "image", Value = "", Description = "Background image for the Rings collection card on the Collections page.",     SortOrder = 1 },
        new() { Key = "collections.image_necklaces", Group = "Collections", Label = "Necklaces card image", Type = "image", Value = "", Description = "Background image for the Necklaces collection card.", SortOrder = 2 },
        new() { Key = "collections.image_bracelets", Group = "Collections", Label = "Bracelets card image", Type = "image", Value = "", Description = "Background image for the Bracelets collection card.", SortOrder = 3 },
        new() { Key = "collections.image_earrings",  Group = "Collections", Label = "Earrings card image",  Type = "image", Value = "", Description = "Background image for the Earrings collection card.", SortOrder = 4 },

        // ── Homepage Feature (two-up "Icons of Summer"-style section under Shop by Category) ──
        new() { Key = "home.feature.enabled",     Group = "Homepage Feature", Label = "Show Feature Section",   Type = "boolean",  Value = "true",            Description = "Show the two-image feature section under Shop by Category.",              SortOrder = 0 },
        new() { Key = "home.feature.heading",     Group = "Homepage Feature", Label = "Section Heading",        Type = "text",     Value = "Icons of Summer", Description = "Centred heading above the two images.",                                   SortOrder = 1 },

        new() { Key = "home.feature.b1.image",     Group = "Homepage Feature", Label = "Block 1 — Image",        Type = "image",    Value = "",                Description = "Left model photo. Recommended: portrait, at least 800×1000px.",           SortOrder = 2 },
        new() { Key = "home.feature.b1.title",     Group = "Homepage Feature", Label = "Block 1 — Title",        Type = "text",     Value = "",                Description = "Optional heading shown under the image. e.g. \"Knot by Sterlin Glams\".",                                          SortOrder = 3 },
        new() { Key = "home.feature.b1.text",      Group = "Homepage Feature", Label = "Block 1 — Description",  Type = "textarea", Value = "",                Description = "Optional short line shown under the title.",                                             SortOrder = 4 },
        new() { Key = "home.feature.b1.category",  Group = "Homepage Feature", Label = "Block 1 — Links To",     Type = "category", Value = "",                Description = "Required — the image becomes clickable and the button appears only once you pick a category here.",                         SortOrder = 5 },
        new() { Key = "home.feature.b1.link_text", Group = "Homepage Feature", Label = "Block 1 — Button Text",  Type = "text",     Value = "Shop the Collection", Description = "Button label. Only shows when 'Links To' (category) is set.",                                    SortOrder = 6 },

        new() { Key = "home.feature.b2.image",     Group = "Homepage Feature", Label = "Block 2 — Image",        Type = "image",    Value = "",                Description = "Right model photo. Recommended: portrait, at least 800×1000px.",          SortOrder = 7 },
        new() { Key = "home.feature.b2.title",     Group = "Homepage Feature", Label = "Block 2 — Title",        Type = "text",     Value = "",                Description = "Optional heading shown under the image. e.g. \"HardWear by Sterlin Glams\".",                                      SortOrder = 8 },
        new() { Key = "home.feature.b2.text",      Group = "Homepage Feature", Label = "Block 2 — Description",  Type = "textarea", Value = "",                Description = "Optional short line shown under the title.",                                             SortOrder = 9 },
        new() { Key = "home.feature.b2.category",  Group = "Homepage Feature", Label = "Block 2 — Links To",     Type = "category", Value = "",                Description = "Required — the image becomes clickable and the button appears only once you pick a category here.",                         SortOrder = 10 },
        new() { Key = "home.feature.b2.link_text", Group = "Homepage Feature", Label = "Block 2 — Button Text",  Type = "text",     Value = "Shop the Collection", Description = "Button label. Only shows when 'Links To' (category) is set.",                                    SortOrder = 11 },
    };
}
