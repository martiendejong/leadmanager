import { useState, useCallback, useRef, useEffect } from 'react'

export interface UseLeadSelectionResult {
  selectedIds: Set<string>
  anchorId: string | null
  isSelected: (id: string) => boolean
  handleRowClick: (id: string, event: React.MouseEvent) => void
  handleRowMouseDown: (id: string, event: React.MouseEvent) => void
  clearSelection: () => void
  selectAll: (ids: string[]) => void
  isDragging: boolean
  dragRange: { from: string; to: string } | null
}

export function useLeadSelection(orderedIds: string[]): UseLeadSelectionResult {
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set())
  const [anchorId, setAnchorId] = useState<string | null>(null)
  const [isDragging, setIsDragging] = useState(false)
  const [dragRange, setDragRange] = useState<{ from: string; to: string } | null>(null)

  const dragStartIdRef = useRef<string | null>(null)
  const isDraggingRef = useRef(false)
  const orderedIdsRef = useRef(orderedIds)

  // Keep ref in sync
  useEffect(() => {
    orderedIdsRef.current = orderedIds
  }, [orderedIds])

  const getRange = useCallback((fromId: string, toId: string, ids: string[]): string[] => {
    const fromIdx = ids.indexOf(fromId)
    const toIdx = ids.indexOf(toId)
    if (fromIdx === -1 || toIdx === -1) return []
    const start = Math.min(fromIdx, toIdx)
    const end = Math.max(fromIdx, toIdx)
    return ids.slice(start, end + 1)
  }, [])

  const handleRowClick = useCallback(
    (id: string, event: React.MouseEvent) => {
      if (event.ctrlKey || event.metaKey) {
        // Toggle id, don't change anchor
        setSelectedIds((prev) => {
          const next = new Set(prev)
          if (next.has(id)) {
            next.delete(id)
          } else {
            next.add(id)
          }
          return next
        })
      } else if (event.shiftKey && anchorId) {
        // Select range from anchor to id
        const range = getRange(anchorId, id, orderedIds)
        setSelectedIds(new Set(range))
      } else {
        // Plain click: single select, update anchor
        setSelectedIds(new Set([id]))
        setAnchorId(id)
      }
    },
    [anchorId, orderedIds, getRange]
  )

  const handleRowMouseDown = useCallback(
    (id: string, event: React.MouseEvent) => {
      if (event.ctrlKey || event.metaKey || event.shiftKey) return
      // Start potential drag
      dragStartIdRef.current = id
    },
    []
  )

  // Global mousemove / mouseup for drag selection
  useEffect(() => {
    const onMouseMove = (e: MouseEvent) => {
      if (!dragStartIdRef.current) return

      // Find which row we're hovering — rows have data-lead-id attribute
      const target = document.elementFromPoint(e.clientX, e.clientY)
      const row = target?.closest('[data-lead-id]') as HTMLElement | null
      if (!row) return

      const hoveredId = row.getAttribute('data-lead-id')
      if (!hoveredId) return

      if (!isDraggingRef.current) {
        // Only start drag if we've moved to a different row
        if (hoveredId === dragStartIdRef.current) return
        isDraggingRef.current = true
        setIsDragging(true)
      }

      const from = dragStartIdRef.current
      const to = hoveredId
      const range = getRange(from, to, orderedIdsRef.current)
      setSelectedIds(new Set(range))
      setDragRange({ from, to })
    }

    const onMouseUp = () => {
      if (isDraggingRef.current) {
        isDraggingRef.current = false
        setIsDragging(false)
        setDragRange(null)
        // Set anchor to the drag start
        if (dragStartIdRef.current) {
          setAnchorId(dragStartIdRef.current)
        }
      }
      dragStartIdRef.current = null
    }

    document.addEventListener('mousemove', onMouseMove)
    document.addEventListener('mouseup', onMouseUp)
    return () => {
      document.removeEventListener('mousemove', onMouseMove)
      document.removeEventListener('mouseup', onMouseUp)
    }
  }, [getRange])

  const isSelected = useCallback((id: string) => selectedIds.has(id), [selectedIds])

  const clearSelection = useCallback(() => {
    setSelectedIds(new Set())
    setAnchorId(null)
    dragStartIdRef.current = null
  }, [])

  const selectAll = useCallback((ids: string[]) => {
    setSelectedIds(new Set(ids))
  }, [])

  return {
    selectedIds,
    anchorId,
    isSelected,
    handleRowClick,
    handleRowMouseDown,
    clearSelection,
    selectAll,
    isDragging,
    dragRange,
  }
}
