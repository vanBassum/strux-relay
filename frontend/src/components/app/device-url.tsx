import { useState } from "react"
import { CheckIcon, CopyIcon } from "lucide-react"
import { toast } from "sonner"

import { Button } from "@/components/ui/button"
import { devicePipeUrl } from "@/lib/device-url"

export function DeviceUrl() {
  const url = devicePipeUrl()
  const [copied, setCopied] = useState(false)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url)
      setCopied(true)
      // Back to the copy icon on its own: a tick that stays looks like state.
      setTimeout(() => setCopied(false), 1500)
    } catch {
      // Clipboard access needs a secure context, so plain http on a LAN address
      // refuses it. Selecting the text by hand still works, and saying so beats a
      // button that silently does nothing.
      toast.error("Could not copy — select the URL and copy it by hand.")
    }
  }

  return (
    <div className="flex items-center gap-1">
      {/* Unlabelled on purpose. The title says "Devices", so a URL sitting on
          this page is the one a device is pointed at; the aria-label and the
          tooltip carry what the caption used to. */}
      <code
        className="bg-muted rounded-md px-2 py-1.5 font-mono text-xs"
        title="Point a device's relay.url here"
      >
        {url}
      </code>
      <Button
        size="icon"
        variant="ghost"
        aria-label="Copy the device URL"
        onClick={() => void copy()}
      >
        {copied ? <CheckIcon /> : <CopyIcon />}
      </Button>
    </div>
  )
}
