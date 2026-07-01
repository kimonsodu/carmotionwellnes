package com.orbital.phone

import android.app.Activity
import android.content.Context
import android.os.Handler
import android.os.Looper
import com.android.billingclient.api.AcknowledgePurchaseParams
import com.android.billingclient.api.BillingClient
import com.android.billingclient.api.BillingClientStateListener
import com.android.billingclient.api.BillingFlowParams
import com.android.billingclient.api.BillingResult
import com.android.billingclient.api.PendingPurchasesParams
import com.android.billingclient.api.ProductDetails
import com.android.billingclient.api.Purchase
import com.android.billingclient.api.PurchasesUpdatedListener
import com.android.billingclient.api.QueryProductDetailsParams
import com.android.billingclient.api.QueryPurchasesParams

/**
 * Google Play Billing wrapper for Orbital's single subscription, which unlocks REMOTE STREAMING to the
 * Windows app (SensorService). The on-phone cue overlay stays free.
 *
 * Flow: connect -> query the subscription's owned purchases (updates [Entitlements]) + its offers (for
 * the paywall). Purchases are acknowledged (Play auto-refunds an unacknowledged purchase after 3 days).
 * Entitlement is client-side only (no backend) — see [Entitlements].
 *
 * Threading: Play callbacks arrive on the main thread; [onChanged] is posted to the main thread too.
 */
class BillingManager(
    context: Context,
    /** Invoked on the main thread whenever entitlement or the loaded plan list may have changed. */
    private val onChanged: () -> Unit
) : PurchasesUpdatedListener, BillingClientStateListener {

    companion object {
        /** Subscription product id. Create it in Play Console (type: subscription) with monthly/yearly
         *  base plans; the paywall lists whatever base plans/offers you configure. */
        const val SUB_PRODUCT_ID = "orbital_remote"
        private const val MAX_BACKOFF_MS = 15L * 60 * 1000
    }

    private val appContext = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private var reconnectDelayMs = 1000L
    @Volatile private var stopped = false

    /** Loaded lazily from Play; null until product details arrive. */
    @Volatile var productDetails: ProductDetails? = null
        private set

    private val client: BillingClient = BillingClient.newBuilder(appContext)
        .setListener(this)
        .enablePendingPurchases(PendingPurchasesParams.newBuilder().enableOneTimeProducts().build())
        .build()

    fun start() {
        stopped = false
        if (!client.isReady) safeConnect()
        else { queryProductDetails(); refreshPurchases() }
    }

    private fun safeConnect() {
        if (stopped) return
        try { client.startConnection(this) } catch (_: Exception) {}
    }

    override fun onBillingSetupFinished(result: BillingResult) {
        if (result.responseCode == BillingClient.BillingResponseCode.OK) {
            reconnectDelayMs = 1000L
            queryProductDetails()
            refreshPurchases()
        }
    }

    override fun onBillingServiceDisconnected() {
        if (stopped) return
        main.postDelayed({ safeConnect() }, reconnectDelayMs)
        reconnectDelayMs = (reconnectDelayMs * 2).coerceAtMost(MAX_BACKOFF_MS)
    }

    /** Re-query owned subscriptions from Play and mirror the definitive result into [Entitlements]. */
    fun refreshPurchases() {
        if (!client.isReady) { safeConnect(); return }
        val params = QueryPurchasesParams.newBuilder()
            .setProductType(BillingClient.ProductType.SUBS).build()
        client.queryPurchasesAsync(params) { result, purchases ->
            if (result.responseCode != BillingClient.BillingResponseCode.OK) return@queryPurchasesAsync
            var active = false
            for (p in purchases) {
                if (p.purchaseState == Purchase.PurchaseState.PURCHASED &&
                    p.products.contains(SUB_PRODUCT_ID)) {
                    active = true
                    acknowledge(p)
                }
            }
            // Definitive Play answer -> cache. active=true refreshes the grace stamp; active=false is
            // DEBOUNCED (confirmInactive) so a transient account-switch empty result can't revoke a payer.
            if (active) Entitlements.confirmActive(appContext) else Entitlements.confirmInactive(appContext)
            main.post { if (!stopped) onChanged() }
        }
    }

    private fun queryProductDetails() {
        val product = QueryProductDetailsParams.Product.newBuilder()
            .setProductId(SUB_PRODUCT_ID)
            .setProductType(BillingClient.ProductType.SUBS).build()
        val params = QueryProductDetailsParams.newBuilder()
            .setProductList(listOf(product)).build()
        client.queryProductDetailsAsync(params) { result, list ->
            if (result.responseCode == BillingClient.BillingResponseCode.OK)
                productDetails = list.firstOrNull { it.productId == SUB_PRODUCT_ID }
            main.post { if (!stopped) onChanged() }
        }
    }

    override fun onPurchasesUpdated(result: BillingResult, purchases: MutableList<Purchase>?) {
        if (result.responseCode == BillingClient.BillingResponseCode.OK) {
            var active = false
            purchases?.forEach { p ->
                if (p.purchaseState == Purchase.PurchaseState.PURCHASED &&
                    p.products.contains(SUB_PRODUCT_ID)) { active = true; acknowledge(p) }
            }
            if (active) Entitlements.confirmActive(appContext)
            refreshPurchases()   // reconcile full state (also promotes pending -> purchased)
        }
        main.post { if (!stopped) onChanged() }
    }

    private fun acknowledge(p: Purchase) {
        if (p.isAcknowledged) return
        val params = AcknowledgePurchaseParams.newBuilder()
            .setPurchaseToken(p.purchaseToken).build()
        client.acknowledgePurchase(params) { }
    }

    /** A subscription base plan / offer, formatted for the paywall list. */
    data class Plan(val label: String, val price: String, val offerToken: String)

    /** Available offers for the loaded subscription (empty until product details arrive). */
    fun plans(): List<Plan> {
        val offers = productDetails?.subscriptionOfferDetails ?: return emptyList()
        return offers.map { offer ->
            val phases = offer.pricingPhases.pricingPhaseList
            // Show the ONGOING recurring price/period the user actually pays each cycle — NOT a free
            // trial or a cheaper intro phase (showing an intro price as the headline is deceptive
            // pricing and Play can reject it). Fall back to the last phase if no infinite phase exists.
            val recurring = phases.lastOrNull { it.recurrenceMode == ProductDetails.RecurrenceMode.INFINITE_RECURRING }
                ?: phases.lastOrNull()
            Plan(planLabel(offer.basePlanId, recurring?.billingPeriod ?: ""),
                recurring?.formattedPrice ?: "", offer.offerToken)
        }
    }

    private fun planLabel(basePlanId: String, isoPeriod: String): String = when (isoPeriod) {
        "P1W" -> "Weekly"; "P1M" -> "Monthly"; "P3M" -> "Quarterly"
        "P6M" -> "Every 6 months"; "P1Y" -> "Yearly"; else -> basePlanId
    }

    /** Launch Play's checkout for the chosen offer. Returns false if billing isn't ready. */
    fun launch(activity: Activity, offerToken: String): Boolean {
        val pd = productDetails ?: return false
        if (!client.isReady) { safeConnect(); return false }
        val params = BillingFlowParams.newBuilder()
            .setProductDetailsParamsList(listOf(
                BillingFlowParams.ProductDetailsParams.newBuilder()
                    .setProductDetails(pd)
                    .setOfferToken(offerToken)
                    .build()))
            .build()
        return client.launchBillingFlow(activity, params).responseCode ==
            BillingClient.BillingResponseCode.OK
    }

    fun end() {
        stopped = true
        main.removeCallbacksAndMessages(null)   // drop the pending reconnect + any queued onChanged posts (no Activity leak)
        try { client.endConnection() } catch (_: Exception) {}
    }
}
