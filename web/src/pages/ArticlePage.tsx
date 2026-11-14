import { Button } from 'devextreme-react/button'
import { CheckBox } from 'devextreme-react/check-box'
import { LoadIndicator } from 'devextreme-react/load-indicator'
import { TextArea } from 'devextreme-react/text-area'
import notify from 'devextreme/ui/notify'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useArticle, useSaveArticle, type Article } from '../api/articles.ts'
import { useCurrentUser } from '../api/auth.ts'
import { ApiError, errorMessage } from '../api/client.ts'
import { TextField } from '../components/TextField.tsx'

/** Create a new article (/knowledge-base/new) or edit an existing one (/knowledge-base/:id). */
export function ArticlePage() {
  const { id: idParam } = useParams()
  const isNew = idParam === 'new'
  const id = isNew ? null : Number(idParam)
  const article = useArticle(id !== null && Number.isInteger(id) && id > 0 ? id : null)

  if (!isNew && (id === null || !Number.isInteger(id) || (article.error instanceof ApiError && article.error.status === 404))) {
    return (
      <section>
        <h1>Article not found</h1>
        <p>
          <Link to="/knowledge-base">Back to the knowledge base</Link>
        </p>
      </section>
    )
  }

  if (!isNew && article.isPending) {
    return (
      <div className="page-loading">
        <LoadIndicator />
      </div>
    )
  }

  if (!isNew && article.isError) {
    return (
      <p className="form-error" role="alert">
        {errorMessage(article.error)}
      </p>
    )
  }

  // Keyed by id so the form's fields start from the article being opened.
  return <ArticleForm key={idParam} article={isNew ? null : (article.data ?? null)} />
}

function ArticleForm({ article }: { article: Article | null }) {
  const { data: me } = useCurrentUser()
  const isAdmin = me?.role === 'Admin'
  const save = useSaveArticle()
  const navigate = useNavigate()

  const [title, setTitle] = useState(article?.title ?? '')
  const [body, setBody] = useState(article?.body ?? '')
  const [isPublished, setIsPublished] = useState(article?.isPublished ?? false)
  const [autoSendAllowed, setAutoSendAllowed] = useState(article?.autoSendAllowed ?? false)

  const apiError = save.error instanceof ApiError ? save.error : undefined
  const textChanged = article !== null && (title.trim() !== article.title || body.trim() !== article.body)
  // An agent's change to approved text withdraws the approval; say so before they save.
  const willLoseApproval = !isAdmin && article?.autoSendAllowed === true && textChanged

  function submit(event: FormEvent) {
    event.preventDefault()
    if (save.isPending) return
    save.mutate(
      { id: article?.id ?? null, title, body, isPublished, ...(isAdmin ? { autoSendAllowed } : {}) },
      {
        onSuccess: () => {
          notify('Article saved.', 'success', 3000)
          void navigate('/knowledge-base')
        },
      },
    )
  }

  return (
    <section>
      <p className="breadcrumb">
        <Link to="/knowledge-base">Knowledge base</Link> / {article ? article.title : 'New article'}
      </p>
      <h1>{article ? 'Edit article' : 'New article'}</h1>

      <form className="card article-form" onSubmit={submit} noValidate>
        <TextField label="Title" value={title} onChange={setTitle} error={apiError?.fieldError('title')} />

        <div className="field">
          <TextArea
            label="Article"
            labelMode="outside"
            stylingMode="outlined"
            minHeight={280}
            autoResizeEnabled
            maxLength={50000}
            value={body}
            onValueChange={setBody}
            valueChangeEvent="input"
            isValid={!apiError?.fieldError('body')}
            validationError={apiError?.fieldError('body') ? { message: apiError.fieldError('body') } : undefined}
            validationMessageMode="always"
          />
          <div className="field-hint">
            Plain text. Write it the way you would answer the student: suggested replies reuse this wording.
          </div>
        </div>

        <div className="field">
          <CheckBox
            text="Published (found by search and used for suggested answers)"
            value={isPublished}
            onValueChange={(value: boolean | null) => setIsPublished(value === true)}
          />
        </div>

        <div className="field">
          <CheckBox
            text="Approved for auto-send"
            value={willLoseApproval ? false : autoSendAllowed}
            disabled={!isAdmin}
            onValueChange={(value: boolean | null) => setAutoSendAllowed(value === true)}
          />
          <div className="field-hint">
            {isAdmin
              ? 'An answer built only from approved articles can be sent to the student without an agent reading it first. Approve only low-risk facts, never refunds, billing, complaints, or exceptions to policy.'
              : willLoseApproval
                ? 'This article is approved for auto-send. Saving your changes to its text withdraws the approval until an admin approves it again.'
                : 'Only an admin can approve an article for auto-send.'}
          </div>
        </div>

        {save.isError && !apiError?.fieldError('title') && !apiError?.fieldError('body') && (
          <p className="form-error" role="alert">
            {errorMessage(save.error)}
          </p>
        )}

        <div className="dialog-actions">
          <Button text="Cancel" stylingMode="outlined" onClick={() => void navigate('/knowledge-base')} />
          <Button
            text={save.isPending ? 'Saving…' : 'Save'}
            type="default"
            useSubmitBehavior
            disabled={save.isPending || !title.trim() || !body.trim()}
          />
        </div>
      </form>
    </section>
  )
}
