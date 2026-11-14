import { Button } from 'devextreme-react/button'
import { Column, DataGrid, SearchPanel } from 'devextreme-react/data-grid'
import notify from 'devextreme/ui/notify'
import { Link, useNavigate } from 'react-router'
import { useArticles, useSetArticlePublished, type Article } from '../api/articles.ts'
import { errorMessage } from '../api/client.ts'
import { formatDateTime } from '../format.ts'

export function KnowledgeBasePage() {
  const articles = useArticles()
  const setPublished = useSetArticlePublished()
  const navigate = useNavigate()

  function togglePublished(article: Article) {
    setPublished.mutate(
      { id: article.id, isPublished: !article.isPublished },
      {
        onSuccess: () =>
          notify(`"${article.title}" ${article.isPublished ? 'unpublished' : 'published'}.`, 'success', 3000),
        onError: (error) => notify(errorMessage(error), 'error', 5000),
      },
    )
  }

  return (
    <section>
      <div className="page-header">
        <h1>Knowledge base</h1>
        <Button text="New article" type="default" icon="add" onClick={() => void navigate('/knowledge-base/new')} />
      </div>
      <p className="muted page-intro">
        Suggested answers are built from these articles. Only published articles are found by search. An answer
        built only from articles approved for auto-send may be sent without an agent reviewing it.
      </p>

      {articles.isError && (
        <p className="form-error" role="alert">
          {errorMessage(articles.error)}
        </p>
      )}

      <DataGrid
        dataSource={articles.data ?? []}
        keyExpr="id"
        showBorders
        rowAlternationEnabled
        noDataText={articles.isPending ? 'Loading…' : 'No articles yet.'}
      >
        <SearchPanel visible width={280} placeholder="Filter articles" />
        <Column
          dataField="title"
          caption="Title"
          defaultSortOrder="asc"
          cellRender={({ data }: { data: Article }) => <Link to={`/knowledge-base/${data.id}`}>{data.title}</Link>}
        />
        {/* Not shown, but lets the filter box match words in the article text. */}
        <Column dataField="body" visible={false} />
        <Column
          dataField="isPublished"
          caption="Status"
          width={120}
          allowSearch={false}
          cellRender={({ data }: { data: Article }) =>
            data.isPublished ? (
              <span className="badge badge-active">Published</span>
            ) : (
              <span className="badge badge-deactivated">Draft</span>
            )
          }
        />
        <Column
          dataField="autoSendAllowed"
          caption="Auto-send"
          width={130}
          allowSearch={false}
          cellRender={({ data }: { data: Article }) =>
            data.autoSendAllowed ? (
              <span className="badge badge-status-needshumanreview">Approved</span>
            ) : (
              <span className="muted">Review first</span>
            )
          }
        />
        <Column
          dataField="updatedAt"
          caption="Updated"
          width={230}
          allowSearch={false}
          cellRender={({ data }: { data: Article }) =>
            formatDateTime(data.updatedAt) + (data.updatedByName ? ` · ${data.updatedByName}` : '')
          }
        />
        <Column
          caption=""
          width={120}
          allowSorting={false}
          cellRender={({ data }: { data: Article }) => (
            <div className="row-actions">
              <Button
                text={data.isPublished ? 'Unpublish' : 'Publish'}
                stylingMode="text"
                disabled={setPublished.isPending}
                onClick={() => togglePublished(data)}
              />
            </div>
          )}
        />
      </DataGrid>
    </section>
  )
}
